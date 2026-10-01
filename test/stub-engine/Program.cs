// ============================================================================
// RenamePro 测试替身：桩 soffice.exe（test double，永不随产品发布）
//
// 为什么存在：RenamePro 新增「文档格式转换」能力，真正的转换由外置 LibreOffice
// （soffice.exe）完成；但开发机与 CI 上并没有装 LibreOffice。于是需要这个桩程序冒充
// LibreOffice 的无头 CLI，以便端到端验证我们的集成层：
//   * 引擎探测（--version 的输出形状）
//   * 命令行拼装（-env:UserInstallation / --convert-to / --outdir / 各种无头开关）
//   * 产物命名对账（产物名 = 源文件基名 + 目标扩展名）
//   * 产物格式校验（魔数 / 容器条目）
//   * 超时与进程树杀灭（无限挂起 + 强制退出码）
//
// 本桩模仿的是真实 LibreOffice 的「可观测契约」，只有下面几件事必须逐字一致：
//   1) `soffice --version` → `LibreOffice 24.8.4.2 420(Build:2)`（我们靠它探测引擎）；
//   2) `--convert-to "<EXT>[:<FilterName>[:<FilterOptions>]]"`，第一个冒号前是目标扩展名；
//   3) 产物文件名 = **输入文件的基名** + 目标扩展名，写在 `--outdir` 下 ——
//      这是最重要的一条：调用方必须自己拿「改名后的新名字」去 outdir 找产物、再对账
//      （LibreOffice 从不用别的路径来命名产物）；
//   4) 成功/失败信号 = 退出码 + stderr；真实 LO 还有「退出码 0 但没有产物」的假成功，
//      本桩用 RENAMEPRO_STUB_NO_OUTPUT 复现它。
//
// 唯一的文件级差异：真实 LO 产出的是真文档，本桩产出的是「魔数/容器结构正确、能过格式
// 校验」的最小产物，并把源文件指纹（字节数 + SHA256）写进产物或产物旁边的
// stub-source.txt，这样测试可以证明产物确实来自那个源文件。
//
// 构建 / 变成 soffice.exe：
//   dotnet build                                # 开发期编译（多文件输出）
//   pwsh -File build-stub.ps1                   # 单文件发布 → dist\soffice.exe + dist\soffice.com
//   注意：只有单文件发布的 exe 才能改名为 soffice.exe；普通 build 的 stub-engine.exe
//   依赖同目录的 stub-engine.dll，单独改名拷贝会启动失败。
//
// 环境变量钩子（只改变行为，绝不改变命令行契约 —— 测试无需感知参数细节）：
//   RENAMEPRO_STUB_DELAY_MS   写出产物前睡这么久（模拟慢文档 → 触发调用方超时）
//   RENAMEPRO_STUB_HANG=1     解析完参数后无限睡（测超时 + 进程树杀灭；不写任何产物）
//   RENAMEPRO_STUB_NO_OUTPUT=1 退出码 0 但什么都不写（真实 LO 的假成功）
//   RENAMEPRO_STUB_STDERR     退出前把这行文本写到 stderr
//   RENAMEPRO_STUB_EXIT       强制最终退出码（做完或不做完工作之后生效）
//   RENAMEPRO_STUB_PROFILE_LOG 追加一行「本次调用的 -env:UserInstallation + PID」到该文件
//
// 这里没有任何交互：不读 stdin、不弹窗、不写控制台标题，只作为子进程运行。
// 永远不删除、不修改、不重命名输入文件。
// ============================================================================

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RenamePro.TestStubEngine;

internal static class Program
{
    // ——————————————————————————————————————————————————————————————
    // 契约文本：调用方按这些字符串判断引擎状态，必须逐字一致
    // ——————————————————————————————————————————————————————————————

    /// <summary>真实 LibreOffice 的 --version 输出形状（前缀 "LibreOffice " + 点分版本号 + 构建号）。</summary>
    private const string VersionLine = "LibreOffice 24.8.4.2 420(Build:2)";

    /// <summary>真实 LibreOffice 把 PDF 导进 Draw 后没有 docx 导出过滤器时逐字给出的报错。</summary>
    private const string NoFilterForPdfToDocx = "Error: no filter for pdf -> docx";

    /// <summary>文本类产物里的「可识别标记行」，调用方用它断言产物确实是本桩写的。</summary>
    private const string MarkerPrefix = "RENAMEPRO-STUB-ARTIFACT";

    /// <summary>二进制产物旁边的指纹文件（首行前缀便于调用方清点 outdir 时明确跳过）。</summary>
    private const string SidecarPrefix = "RENAMEPRO-STUB-SIDECAR";
    private const string SidecarFileName = "stub-source.txt";

    /// <summary>UTF-8 无 BOM：产物本身（md/csv/html/xml）绝不能被 BOM 污染。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// UTF-8 带 BOM：只用于桩自己的诊断文件（stub-source.txt / profile 日志）。
    /// 理由：Windows PowerShell 5.1 的 Get-Content 默认按 ANSI(GBK) 读无 BOM 文件，
    /// 里面的中文路径会显示成乱码，让人误以为桩把参数搞坏了。产物本身仍是无 BOM 的 UTF-8。
    /// </summary>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>延迟上限（10 分钟）：防止把钩子设成天文数字导致测试永远吊死。</summary>
    private const int MaxDelayMs = 10 * 60 * 1000;

    /// <summary>PDF 体积上限：按源文件成比例放大，但别让一个 500MB 的源文件撑爆临时目录。</summary>
    private const long MaxPdfBytes = 4L * 1024 * 1024;

    /// <summary>真实 LibreOffice 无头模式必带的开关：静默接受、任意顺序、不产生行为差异。</summary>
    private static readonly string[] SilentlyAcceptedSwitches =
    {
        "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nofirststartwizard", "--nologo"
    };

    // ——————————————————————————————————————————————————————————————
    // 入口
    // ——————————————————————————————————————————————————————————————

    private static int Main()
    {
        UseUtf8Writers();

        // 用 Environment.GetCommandLineArgs()：运行时已经完成引号与转义处理，路径里的空格和中文
        // 都能原样拿到。自己再解析一遍原始命令行只会引入 bug（规范明确要求这么做）。
        ParsedArgs parsed = ParseArgs(CommandLineWithoutExe());

        // 每次调用都追加一行日志：profile URL 证明「每次调用都有自己的 profile」，
        // PID 则让调用方在杀掉进程树之后核对「这些进程确实死了」。
        // 放在最前面，连 --version 探测和挂起进程都会留下记录。
        AppendProfileLog(parsed.ProfileUrl);

        // --version：引擎探测走这条路径。真实 LO 把版本打到 stdout，退出码 0。
        // 注意：环境变量钩子刻意不作用于 --version/--help（它们是纯内省调用，钩子只用来驱动转换路径），
        // 否则一个全局设置的 RENAMEPRO_STUB_EXIT 会让引擎探测本身就失败。
        if (parsed.WantsVersion)
        {
            Console.Out.WriteLine(VersionLine);
            return 0;
        }

        // --help：真实 LO 打一段用法；这里给短版，并附上本桩的钩子说明便于排障。
        if (parsed.WantsHelp)
        {
            PrintUsage();
            return 0;
        }

        return RunConversion(parsed);
    }

    /// <summary>命令行参数（不含第 0 个「可执行文件路径」）。</summary>
    private static List<string> CommandLineWithoutExe()
    {
        string[] all = Environment.GetCommandLineArgs();
        var args = new List<string>(all.Length > 0 ? all.Length - 1 : 0);
        for (int i = 1; i < all.Length; i++) args.Add(all[i]);
        return args;
    }

    // ——————————————————————————————————————————————————————————————
    // 参数解析（宽容但不猜：只认契约里有的东西，其余开关一律忽略）
    // ——————————————————————————————————————————————————————————————

    private sealed class ParsedArgs
    {
        /// <summary>`--convert-to` 的原始值，例如 `pdf:writer_pdf_Export`。</summary>
        public string? ConvertTo { get; set; }

        /// <summary>`--outdir` 的值。</summary>
        public string? OutDir { get; set; }

        /// <summary>`-env:UserInstallation=...` 的值（每次调用独立的用户 profile）。</summary>
        public string? ProfileUrl { get; set; }

        /// <summary>最后一个位置参数：输入文件（真实 LO 支持多个源，这里按契约只取最后一个）。</summary>
        public string? InputPath { get; set; }

        public bool WantsVersion { get; set; }
        public bool WantsHelp { get; set; }
    }

    private static ParsedArgs ParseArgs(List<string> args)
    {
        var parsed = new ParsedArgs();
        var positionals = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];

            if (token is "--version" or "-version" or "-v")
            {
                parsed.WantsVersion = true;
                continue;
            }

            if (token is "--help" or "-help" or "-h" or "-?" or "/?")
            {
                parsed.WantsHelp = true;
                continue;
            }

            // -env:UserInstallation=file:///C:/temp/profile —— 真实 LO 用它把「用户配置目录」指到
            // 临时位置，保证并发转换互不干扰（同一 profile 不能同时被两个实例打开）。
            // 桩只把它记录下来（写进 profile 日志与指纹），不真的创建 profile 目录。
            if (TryMatchEnvSetting(token, out string envName, out string envValue))
            {
                if (envName.Equals("UserInstallation", StringComparison.OrdinalIgnoreCase))
                    parsed.ProfileUrl = envValue;
                continue;
            }

            // --convert-to <EXT>[:<Filter>[:<Options>]]（同时也吃真实 LO 支持的 --convert-to=... 写法）
            if (IsSwitch(token, "--convert-to"))
            {
                parsed.ConvertTo = TakeSwitchValue(args, ref i, token, "--convert-to");
                continue;
            }

            if (IsSwitch(token, "--outdir"))
            {
                parsed.OutDir = TakeSwitchValue(args, ref i, token, "--outdir");
                continue;
            }

            // 无头模式的固定开关：接受但没有任何行为差异。
            if (SilentlyAcceptedSwitches.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;

            // 其它 -/-- 开头的 token 一律忽略：桩不是参数校验器，将来调用方新增 LO 开关时
            // 不应该让测试夹具先崩掉（真实 LO 对未知开关也只是警告）。
            if (token.Length > 1 && token[0] == '-') continue;

            positionals.Add(token);
        }

        // 真实 LO 的最后一个位置参数就是要转换的文档。
        parsed.InputPath = positionals.Count > 0 ? positionals[^1] : null;
        return parsed;
    }

    /// <summary>token 是 `name` 本身，或 `name=value`。</summary>
    private static bool IsSwitch(string token, string name) =>
        token.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (token.Length > name.Length && token[name.Length] == '=' &&
         token.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>取开关的值：`--switch value` 或 `--switch=value`；缺失时返回 null。</summary>
    private static string? TakeSwitchValue(List<string> args, ref int index, string token, string name)
    {
        if (token.Length > name.Length && token[name.Length] == '=') return token[(name.Length + 1)..];
        if (index + 1 < args.Count)
        {
            index++;
            return args[index];
        }
        return null;
    }

    /// <summary>识别 `-env:Name=Value` / `--env:Name=Value`（真实 LO 两种前缀都接受）。</summary>
    private static bool TryMatchEnvSetting(string token, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        int prefixLength = token.StartsWith("-env:", StringComparison.OrdinalIgnoreCase) ? 5
                         : token.StartsWith("--env:", StringComparison.OrdinalIgnoreCase) ? 6
                         : -1;
        if (prefixLength < 0) return false;

        int equals = token.IndexOf('=', prefixLength);
        if (equals < 0)
        {
            name = token[prefixLength..];
            return true;
        }

        name = token[prefixLength..equals];
        value = token[(equals + 1)..];
        return true;
    }

    // ——————————————————————————————————————————————————————————————
    // 转换路径
    // ——————————————————————————————————————————————————————————————

    private static int RunConversion(ParsedArgs parsed)
    {
        // ① 参数校验：退出码 1 + 中文可操作提示。
        //    真实 LO 的约定是「非 0 退出码 = 这次转换没成功」，stderr 写人能看懂的原因。
        if (string.IsNullOrWhiteSpace(parsed.ConvertTo))
            return Fail(1, "错误：缺少 --convert-to 参数。" +
                           "正确用法：soffice --headless -env:UserInstallation=<URL> " +
                           "--convert-to \"pdf:writer_pdf_Export\" --outdir \"<输出目录>\" \"<源文件>\"");

        (string targetExt, string? filterName, string? filterOptions) = SplitConvertTo(parsed.ConvertTo!);
        if (targetExt.Length == 0)
            return Fail(1, "错误：--convert-to 的目标扩展名为空（示例：\"pdf\" 或 \"pdf:writer_pdf_Export\"）");

        if (string.IsNullOrWhiteSpace(parsed.OutDir))
            return Fail(1, "错误：缺少 --outdir 参数（LibreOffice 默认写到当前目录，本集成要求显式指定输出目录）");

        if (string.IsNullOrWhiteSpace(parsed.InputPath))
            return Fail(1, "错误：没有指定输入文件（最后一个位置参数应当是源文件路径）");

        if (!File.Exists(parsed.InputPath))
            return Fail(1, $"错误：找不到输入文件：{parsed.InputPath}");

        string sourceExt = ExtensionNoDot(parsed.InputPath!);

        // ② PDF → DOCX 没有可用过滤器：真实 LO 把 PDF 导入 Draw，而 Draw 没有 docx 导出过滤器，
        //    于是它明确打出这行英文并返回非 0（不是 1 这种通用失败码）。逐字复现，
        //    调用方按这行字符串断言「引擎明确拒绝」而不是「产物没生成」。
        //    优先级：这条契约错误优先于 RENAMEPRO_STUB_EXIT（见第 ⑥ 步注释）。
        if (sourceExt == "pdf" && targetExt == "docx")
        {
            Console.Error.WriteLine(NoFilterForPdfToDocx);
            return 3;
        }

        // ③ 钩子：HANG —— 在写产物之前无限睡，专门用来测「超时 + 进程树杀灭」。
        //    刻意吞掉 Ctrl+C（e.Cancel = true）：既保证它绝不会把 Ctrl+C 变成「成功」并写出产物，
        //    也保证它只能被真正的 TerminateProcess / Kill(entireProcessTree) 结束 ——
        //    这正是调用方要验证的那条路径（合作式信号杀不掉它）。
        if (EnvFlag("RENAMEPRO_STUB_HANG"))
        {
            Console.CancelKeyPress += static (_, e) => e.Cancel = true;
            while (true) Thread.Sleep(1000);
        }

        // ④ 钩子：DELAY —— 模拟「大文档转换很慢」，让调用方的时间预算逻辑先触发。
        int? delayMs = EnvInt("RENAMEPRO_STUB_DELAY_MS");
        if (delayMs is > 0) Thread.Sleep(Math.Min(delayMs.Value, MaxDelayMs));

        // ⑤ 产物路径：真实 LO 的命名规则 —— 输入文件的基名 + 目标扩展名，写进 --outdir。
        //    这是本桩最重要的行为：调用方必须先按「新名字」去 outdir 找产物再做对账，
        //    而不是拿旧路径或旧文件名去猜。
        string outPath = Path.Combine(
            parsed.OutDir!,
            Path.GetFileNameWithoutExtension(parsed.InputPath!) + "." + targetExt);

        int exitCode = 0;

        if (EnvFlag("RENAMEPRO_STUB_NO_OUTPUT"))
        {
            // 真实 LO 的假成功：退出码 0、stderr 可能干干净净，但 outdir 里什么都没有。
            // 调用方必须能识别这种情形，否则会把「只改了扩展名、内容没变」当成转换成功。
            exitCode = 0;
        }
        else
        {
            // 真实 LO 要求 --outdir 已经存在；桩顺手创建，省掉测试脚本先建目录这一步
            // （这是与真实 LO 的已知差异，已在注释里写明）。
            Directory.CreateDirectory(parsed.OutDir!);

            // 只读输入：真实 LO 也不会动源文件。这里绝不删除/修改/重命名源文件。
            byte[] sourceBytes = File.ReadAllBytes(parsed.InputPath!);
            string sha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

            var info = new SourceInfo(
                FullPath: Path.GetFullPath(parsed.InputPath!),
                SourceExt: sourceExt,
                Family: FamilyOf(sourceExt),
                ByteLength: sourceBytes.LongLength,
                Sha256: sha256,
                TargetExt: targetExt,
                FilterName: filterName,
                FilterOptions: filterOptions,
                ProfileUrl: parsed.ProfileUrl);

            bool knownFormat = BuildArtifact(info, outPath);
            if (!knownFormat)
            {
                // 目标扩展名未知：契约要求写纯文本占位文件、stderr 警告、但仍然退出 0。
                // 目的很明确 —— 让调用方测试「产物无法通过格式校验时必须拒绝」这条路径
                // （真实 LO 此时会报 "Error: no filter for ..." 并非 0 退出）。
                Console.Error.WriteLine(
                    $"警告：目标扩展名 \"{targetExt}\" 没有已知的导出过滤器，已写出纯文本占位文件（不可通过格式校验）。" +
                    "（真实 LibreOffice 会报 \"Error: no filter for ...\" 并非 0 退出；桩按测试契约继续返回 0。）");
            }

            // 真实 LO 成功时会往 stdout 打这一行（含所用过滤器）。照抄格式，调用方可选择解析。
            Console.Out.WriteLine(filterName is null
                ? $"convert {parsed.InputPath} -> {outPath}"
                : $"convert {parsed.InputPath} -> {outPath} using filter : {filterName}");
        }

        // ⑥ 钩子：自定义 stderr 文本 + 强制退出码（在「做完/没做工作」之后生效）。
        //    优先级说明：参数校验失败（1）与 pdf→docx 过滤器错误（3）属于契约行为，
        //    优先返回；本钩子只覆盖「成功路径」的最终退出码（含 NO_OUTPUT 的场景），
        //    用来模拟「引擎跑完了但调用了方必须当作失败」的各种退出码。
        string? stderrText = Environment.GetEnvironmentVariable("RENAMEPRO_STUB_STDERR");
        if (!string.IsNullOrEmpty(stderrText)) Console.Error.WriteLine(stderrText);

        int? forcedExitCode = EnvInt("RENAMEPRO_STUB_EXIT");
        if (forcedExitCode is not null) exitCode = forcedExitCode.Value;

        return exitCode;
    }

    /// <summary>把 `EXT[:Filter[:Options]]` 拆开：只按第一个冒号切扩展名，其余整体归过滤器/选项。</summary>
    private static (string Ext, string? Filter, string? Options) SplitConvertTo(string value)
    {
        string[] parts = value.Split(':', 3);

        // 扩展名去掉可能的前导点并统一小写：调用方按小写扩展名组织产物名，产物名里的扩展名
        // 也必须是它期望的那个（真实 LO 用 --convert-to 里给的扩展名拼产物名）。
        string ext = parts[0].Trim().TrimStart('.').ToLowerInvariant();

        string? filter = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;

        // 过滤器选项里可能含逗号（如 CSV 的 "44,34,76,1"），原样保留。
        string? options = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

        return (ext, filter, options);
    }

    /// <summary>小写、不带点的扩展名（没有扩展名时返回空串）。</summary>
    private static string ExtensionNoDot(string path)
    {
        string ext = Path.GetExtension(path);
        return (ext.StartsWith('.') ? ext[1..] : ext).ToLowerInvariant();
    }

    /// <summary>源文档族：真实 LO 用它决定由哪个组件（Writer/Impress/Calc/Draw）打开文档。</summary>
    private enum DocumentFamily
    {
        Writer,
        Impress,
        Calc,
        Pdf,
        Unknown
    }

    /// <summary>源扩展名 → 文档族（与 RenamePro 的文档矩阵保持一致）。</summary>
    private static DocumentFamily FamilyOf(string ext) => ext switch
    {
        "docx" or "doc" or "docm" or "dotx" or "odt" or "ott" or "rtf"
            or "txt" or "md" or "markdown" or "html" or "htm" or "xhtml" => DocumentFamily.Writer,
        "pptx" or "pptm" or "potx" or "ppt" or "odp" or "otp" => DocumentFamily.Impress,
        "xlsx" or "xlsm" or "xltx" or "xls" or "ods" or "ots" or "csv" or "tsv" => DocumentFamily.Calc,
        "pdf" => DocumentFamily.Pdf,
        _ => DocumentFamily.Unknown
    };

    private static string FamilyName(DocumentFamily family) => family switch
    {
        DocumentFamily.Writer => "Writer",
        DocumentFamily.Impress => "Impress",
        DocumentFamily.Calc => "Calc",
        DocumentFamily.Pdf => "PDF",
        _ => "Unknown"
    };

    // ——————————————————————————————————————————————————————————————
    // 产物生成
    // ——————————————————————————————————————————————————————————————

    /// <summary>一次转换的源信息，用来生成「源指纹」（证明产物来自哪个源文件）。</summary>
    private sealed record SourceInfo(
        string FullPath,
        string SourceExt,
        DocumentFamily Family,
        long ByteLength,
        string Sha256,
        string TargetExt,
        string? FilterName,
        string? FilterOptions,
        string? ProfileUrl);

    /// <summary>
    /// 按目标扩展名产出「结构正确的最小产物」。
    /// </summary>
    /// <returns>目标扩展名是否有已知格式（false = 写了纯文本占位文件，调用方的格式校验应当拒绝它）。</returns>
    private static bool BuildArtifact(SourceInfo info, string outPath)
    {
        string fingerprint = FingerprintText(info);

        switch (info.TargetExt)
        {
            // PDF：二进制容器 → 指纹既内嵌成 PDF 注释，也放在旁边的 stub-source.txt。
            case "pdf":
                WritePdfArtifact(outPath, info, fingerprint);
                WriteSidecar(outPath, info, fingerprint);
                return true;

            // OOXML / ODF：真 ZIP。规范只列了 docx/pptx/xlsx/odt/ods/odp，这里把同容器的
            // 模板与宏兄弟（dotx/docm/ott/ots/xltx ...）一并映射，否则文档矩阵里合法的
            // odt→ott、xlsx→xltx 这类目标会被误判成「未知格式」。
            case "docx" or "docm" or "dotx":
            case "xlsx" or "xlsm" or "xltx":
            case "pptx" or "pptm" or "potx":
            case "odt" or "ott" or "ods" or "ots" or "odp" or "otp":
                WriteZipArtifact(outPath, info.TargetExt, fingerprint);
                WriteSidecar(outPath, info, fingerprint);
                return true;

            // 老二进制格式：OLE2 复合文档 → 只复现魔数（见 WriteOle2Artifact 注释）。
            case "doc" or "xls" or "ppt":
                WriteOle2Artifact(outPath, fingerprint);
                WriteSidecar(outPath, info, fingerprint);
                return true;

            // RTF：文本方言，指纹写在文件内部。
            case "rtf":
                WriteRtfArtifact(outPath, info.TargetExt, fingerprint);
                return true;

            // 纯文本族：UTF-8 文本，含可识别标记行 + 指纹。
            case "txt" or "md" or "markdown" or "csv" or "tsv" or "html" or "htm" or "xhtml":
                WriteTextArtifact(outPath, info.TargetExt, fingerprint);
                return true;

            // 未知目标扩展名：纯文本占位文件（调用方应当拒绝它）。
            default:
                WriteUnknownTextArtifact(outPath, info.TargetExt, fingerprint);
                return false;
        }
    }

    /// <summary>
    /// 「源指纹」：源文件字节数 + SHA256，外加本次调用的过滤器与 profile。
    /// 调用方据此证明产物确实由这个源文件产生（而不是上一轮的残留文件）。
    /// </summary>
    private static string FingerprintText(SourceInfo info) => string.Join('\n', new[]
    {
        "RenamePro stub-engine artifact fingerprint",
        $"source={info.FullPath}",
        $"sourceBytes={info.ByteLength}",
        $"sourceSha256={info.Sha256}",
        $"sourceFamily={FamilyName(info.Family)}",
        $"targetExt={info.TargetExt}",
        $"filter={info.FilterName ?? "(default)"}",
        $"filterOptions={info.FilterOptions ?? "(none)"}",
        $"userInstallation={info.ProfileUrl ?? "(none)"}",
        $"pid={Environment.ProcessId}"
    });

    /// <summary>标记行：调用方用于断言「这是桩写的产物」。</summary>
    private static string MarkerLine(string ext) => $"{MarkerPrefix} ext={ext}";

    /// <summary>
    /// 二进制/ZIP 产物的指纹落地点：产物旁边的 stub-source.txt。
    /// 为什么不塞进产物内部：往 ZIP 里加条目会改变容器清单，往 OLE2 里加数据会破坏结构。
    /// 首行带 RENAMEPRO-STUB-SIDECAR 前缀，调用方清点 outdir 时可以明确跳过它。
    /// </summary>
    private static void WriteSidecar(string artifactPath, SourceInfo info, string fingerprint)
    {
        string dir = Path.GetDirectoryName(artifactPath) is { Length: > 0 } d ? d : ".";
        string path = Path.Combine(dir, SidecarFileName);

        var sb = new StringBuilder();
        sb.Append(SidecarPrefix).Append(" target=").Append(info.TargetExt).Append('\n');
        sb.Append("artifact=").Append(Path.GetFileName(artifactPath)).Append('\n');
        sb.Append(fingerprint).Append('\n');

        File.WriteAllText(path, sb.ToString(), Utf8WithBom);
    }

    // —— PDF ——————————————————————————————————————————————————————

    /// <summary>
    /// 最小但结构自洽的 PDF：5 个对象 + 交叉引用表 + trailer，开头 `%PDF-1.7`、结尾 `%%EOF`。
    /// 体积做到与源文件「大致成比例」：真实转换后的 PDF 通常比源文件大一点，
    /// 调用方用「产物太小 = 空壳」做判断时不会误报。填充全都用 `%` 注释行，不影响 PDF 结构。
    /// </summary>
    private static void WritePdfArtifact(string outPath, SourceInfo info, string fingerprint)
    {
        long targetBytes = Math.Clamp(2048 + info.ByteLength * 2, 4096, MaxPdfBytes);

        using var buffer = new MemoryStream();
        Ascii(buffer, "%PDF-1.7\n");

        // 真实 PDF 的第 2 行是 4 个 >0x7F 的「二进制标记」字节：告诉工具链「这是二进制文件，
        // 传输时别做文本转换」。照抄，让产物在只看前几行的工具眼里也是真 PDF。
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        long[] offsets = new long[6]; // 1..5 号对象的字节偏移（xref 表要用）
        string content = "BT /F1 12 Tf 72 770 Td (RenamePro stub artifact) Tj ET\n";

        void Object(int number, string body)
        {
            offsets[number] = buffer.Position;
            Ascii(buffer, number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + body + "\nendobj\n");
        }

        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R " +
                  "/Resources << /Font << /F1 5 0 R >> >> >>");
        Object(4, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n" + content + "endstream");
        Object(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // 指纹内嵌成 PDF 注释（注释里的非 ASCII 字节会被阅读器跳过，不影响打开）。
        Utf8(buffer, "% " + SidecarPrefix + "\n");
        foreach (string line in LinesOf(fingerprint)) Utf8(buffer, "% " + line + "\n");

        int paddingIndex = 0;
        while (buffer.Length < targetBytes - 256)
        {
            Utf8(buffer, string.Create(CultureInfo.InvariantCulture,
                $"% padding {paddingIndex++:D8} ") + new string('=', 40) + "\n");
        }

        long xrefOffset = buffer.Position; // startxref 指向 "xref" 关键字的字节偏移
        Ascii(buffer, "xref\n0 6\n");
        Ascii(buffer, "0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++)
            Ascii(buffer, string.Create(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n"));
        Ascii(buffer, "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" +
                      xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        File.WriteAllBytes(outPath, buffer.ToArray());
    }

    // —— OOXML / ODF 容器 ——————————————————————————————————————————

    /// <summary>
    /// 真 ZIP（System.IO.Compression），第一个条目固定是 `[Content_Types].xml`，
    /// 再放目标格式的主部件（word/document.xml / ppt/presentation.xml / xl/workbook.xml /
    /// ODF 的 mimetype）。这就是调用方格式校验所依赖的容器形状。
    /// </summary>
    private static void WriteZipArtifact(string outPath, string ext, string fingerprint)
    {
        using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        AddZipEntry(zip, "[Content_Types].xml", ContentTypesXml(ext));

        switch (ext)
        {
            case "docx" or "docm" or "dotx":
                AddZipEntry(zip, "word/document.xml", WordDocumentXml(fingerprint, ext));
                break;
            case "xlsx" or "xlsm" or "xltx":
                AddZipEntry(zip, "xl/workbook.xml", WorkbookXml());
                break;
            case "pptx" or "pptm" or "potx":
                AddZipEntry(zip, "ppt/presentation.xml", PresentationXml());
                break;
            default:
                // ODF 的「身份证」就是这个条目的内容（不带换行）；按真实 ODF 用 STORED 存放。
                // 已知差异：真 ODF 要求 mimetype 是**第一个**且不压缩，而调用方的容器校验要求
                // 第一个条目是 [Content_Types].xml，本桩按调用方的规则来（见报告中的保真度说明）。
                AddZipEntry(zip, "mimetype", OdMimetype(ext), CompressionLevel.NoCompression);
                AddZipEntry(zip, "content.xml", OdContentXml(ext, fingerprint));
                break;
        }
    }

    private static void AddZipEntry(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, level);

        // 固定时间戳：同一份输入重复转换得到逐字节相同的产物，测试可以直接比对产物哈希。
        entry.LastWriteTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using Stream stream = entry.Open();
        byte[] bytes = Utf8NoBom.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ContentTypesXml(string ext)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n");
        sb.Append("  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\n");
        sb.Append("  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n");

        // 真正的 ODF（odt/ods/odp）里根本没有 [Content_Types].xml —— 这里为 ODF 也放一个最小版本，
        // 纯粹是因为调用方的容器校验把「首个条目 = [Content_Types].xml」当作 OOXML 类产物的判据。
        if (TryOoxmlPart(ext, out string? part, out string? contentType))
            sb.Append($"  <Override PartName=\"/{part}\" ContentType=\"{contentType}\"/>\n");

        sb.Append("</Types>\n");
        return sb.ToString();
    }

    /// <summary>OOXML 家族的主部件名与内容类型；ODF 家族返回 false。</summary>
    private static bool TryOoxmlPart(string ext, out string? part, out string? contentType)
    {
        switch (ext)
        {
            case "docx" or "docm" or "dotx":
                part = "word/document.xml";
                contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
                return true;
            case "xlsx" or "xlsm" or "xltx":
                part = "xl/workbook.xml";
                contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
                return true;
            case "pptx" or "pptm" or "potx":
                part = "ppt/presentation.xml";
                contentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
                return true;
            default:
                part = null;
                contentType = null;
                return false;
        }
    }

    /// <summary>最小 WordprocessingML：标记行 + 指纹各占一个段落（合法 WML，python-docx 之类也能打开）。</summary>
    private static string WordDocumentXml(string fingerprint, string ext)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
        sb.Append("<w:body>\n");
        foreach (string line in LinesOf(MarkerLine(ext) + "\n" + fingerprint))
            sb.Append("  <w:p><w:r><w:t xml:space=\"preserve\">")
              .Append(XmlEscape(line))
              .Append("</w:t></w:r></w:p>\n");
        sb.Append("  <w:sectPr/>\n</w:body></w:document>\n");
        return sb.ToString();
    }

    private static string WorkbookXml()
    {
        // 只保证「它是 SpreadsheetML 且含 /xl/workbook.xml」；指纹放在旁边的 stub-source.txt。
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
               "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
               "<sheets/></workbook>\n";
    }

    private static string PresentationXml()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
               "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
               "<p:sldIdLst/><p:sldSz cx=\"9144000\" cy=\"6858000\"/></p:presentation>\n";
    }

    /// <summary>ODF 的 mimetype 内容（真实 ODF 容器就靠这一串字节识别格式），不带换行。</summary>
    private static string OdMimetype(string ext) => ext switch
    {
        "ods" => "application/vnd.oasis.opendocument.spreadsheet",
        "ots" => "application/vnd.oasis.opendocument.spreadsheet-template",
        "odp" => "application/vnd.oasis.opendocument.presentation",
        "otp" => "application/vnd.oasis.opendocument.presentation-template",
        "ott" => "application/vnd.oasis.opendocument.text-template",
        _ => "application/vnd.oasis.opendocument.text"
    };

    private static string OdContentXml(string ext, string fingerprint)
    {
        bool spreadsheet = ext is "ods" or "ots";
        bool presentation = ext is "odp" or "otp";

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<office:document-content")
          .Append(" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"")
          .Append(" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"")
          .Append(" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"")
          .Append(" office:version=\"1.3\">\n<office:body>\n");

        if (spreadsheet)
        {
            sb.Append("  <office:spreadsheet><table:table table:name=\"Sheet1\"/></office:spreadsheet>\n");
        }
        else if (presentation)
        {
            sb.Append("  <office:presentation/>\n");
        }
        else
        {
            sb.Append("  <office:text>\n");
            foreach (string line in LinesOf(MarkerLine(ext) + "\n" + fingerprint))
                sb.Append("    <text:p>").Append(XmlEscape(line)).Append("</text:p>\n");
            sb.Append("  </office:text>\n");
        }

        sb.Append("</office:body>\n</office:document-content>\n");
        return sb.ToString();
    }

    // —— OLE2（老 doc/xls/ppt）—————————————————————————————————————

    /// <summary>
    /// 真实 .doc/.xls/.ppt 是 OLE2 复合文档（CFB）。本桩只复现调用方判定「是不是老二进制格式」
    /// 用的 OLE2 魔数 `D0 CF 11 E0 A1 B1 1A E1` 和一个自洽的 512 字节头部骨架（扇区大小、
    /// FAT/目录扇区指针、DIFAT 链尾），不做真的 CFB 目录与扇区链。
    /// 已知保真度缺口：谁要是深入解析这个文件的 CFB 结构，它并不合格（见报告）。
    /// </summary>
    private static void WriteOle2Artifact(string outPath, string fingerprint)
    {
        byte[] header = new byte[512];

        // 0x00：OLE2 魔数（8 字节）
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        // 0x18：minor version 0x003E（62）
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x003E);
        // 0x1A：major version 0x0003（表示 512 字节扇区）
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 0x0003);
        // 0x1C：字节序标记 0xFFFE = little endian
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        // 0x1E：扇区大小 2^9 = 512；0x20：mini 扇区大小 2^6 = 64
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        // 0x2C：FAT 扇区数 = 1；0x30：首个目录扇区 = 1
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), 1);
        // 0x38：mini stream 阈值 = 4096；0x3C/0x40：首个 mini FAT 扇区 / mini FAT 扇区数
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), 1);
        // 0x44：首个 DIFAT 扇区 = ENDOFCHAIN(0xFFFFFFFE)；0x48：DIFAT 扇区数 = 0
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), 0xFFFFFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0);
        // 0x4C 起：109 个 DIFAT 项 —— 第 0 项指向 FAT 扇区 0，其余 FREE(0xFFFFFFFF)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C), 0);
        for (int i = 1; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), 0xFFFFFFFF);

        using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(header, 0, header.Length);

        // 指纹：二进制产物，调用方读旁边的 stub-source.txt；这里再以注释形式附一份，便于人工排障。
        byte[] tail = Utf8NoBom.GetBytes(
            "\n% " + SidecarPrefix + "\n" + fingerprint + "\n");
        file.Write(tail, 0, tail.Length);
    }

    // —— 文本族 ————————————————————————————————————————————————————

    /// <summary>
    /// txt/md/markdown/csv/tsv/html(xhtml)：UTF-8 文本，含可识别标记行 + 源指纹。
    /// HTML 刻意先打 `<!DOCTYPE html>`（真实 LO 的 HTML 导出也是这样开头），
    /// 标记行放在第 2 行的 HTML 注释里、正文里也再出现一次，保证「grep 标记」两种写法都能命中。
    /// </summary>
    private static void WriteTextArtifact(string outPath, string ext, string fingerprint)
    {
        var sb = new StringBuilder();

        if (ext is "html" or "htm" or "xhtml")
        {
            sb.Append("<!DOCTYPE html>\n");
            sb.Append("<!-- ").Append(MarkerLine(ext)).Append(" -->\n");
            sb.Append("<!--\n");
            foreach (string line in LinesOf(fingerprint)) sb.Append("  ").Append(line).Append('\n');
            sb.Append("-->\n");
            sb.Append("<html><head><meta charset=\"utf-8\"><title>RenamePro stub artifact</title></head>");
            sb.Append("<body><p>").Append(XmlEscape(MarkerLine(ext))).Append("</p></body></html>\n");
        }
        else
        {
            sb.Append(MarkerLine(ext)).Append('\n');
            foreach (string line in LinesOf(fingerprint)) sb.Append(line).Append('\n');
        }

        // UTF-8 无 BOM：Markdown/CSV 带 BOM 会被别家工具当成内容。
        File.WriteAllText(outPath, sb.ToString(), Utf8NoBom);
    }

    /// <summary>未知目标扩展名：写纯文本占位文件（格式校验应当拒绝它），但仍按契约退出 0。</summary>
    private static void WriteUnknownTextArtifact(string outPath, string ext, string fingerprint)
    {
        var sb = new StringBuilder();
        sb.Append($"{MarkerPrefix} UNKNOWN-FORMAT ext={ext}\n");
        sb.Append("这是一个占位文件：桩引擎没有该目标扩展名对应的过滤器，产物不是有效文档格式。\n");
        foreach (string line in LinesOf(fingerprint)) sb.Append(line).Append('\n');
        File.WriteAllText(outPath, sb.ToString(), Utf8NoBom);
    }

    /// <summary>RTF：必须 `{\rtf1` 开头。RTF 是 ASCII 方言，非 ASCII 字符要写成 `\uN?`，否则阅读器乱码。</summary>
    private static void WriteRtfArtifact(string outPath, string ext, string fingerprint)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\viewkind4\uc1\pard\f0\fs20 ");
        AppendRtfEscaped(sb, MarkerLine(ext));
        sb.Append(@"\par ");
        AppendRtfEscaped(sb, fingerprint);
        sb.Append(@"\par}").Append('\n');
        File.WriteAllText(outPath, sb.ToString(), Utf8NoBom);
    }

    private static void AppendRtfEscaped(StringBuilder sb, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\r': break;
                case '\n': sb.Append(@"\par "); break;
                default:
                    if (c < 0x80) sb.Append(c);
                    // RTF 的 \uN? 用「有符号 16 位十进制」，所以中文要走 short 再取负号。
                    else sb.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
    }

    // ——————————————————————————————————————————————————————————————
    // 环境变量钩子
    // ——————————————————————————————————————————————————————————————

    /// <summary>开关型钩子：值 "1"/"true"/"yes"（忽略大小写、前后空格）视为开；其余（含空、0）视为关。</summary>
    private static bool EnvFlag(string name)
    {
        string value = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return value is "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>整数型钩子：未设置或为空返回 null（注意 "0" 是有效值，与「未设置」不同）。</summary>
    private static int? EnvInt(string name)
    {
        string raw = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        if (raw.Length == 0) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;

        // 写错钩子值是最常见的测试事故，直接告警而不是静默忽略。
        Console.Error.WriteLine($"警告：环境变量 {name}=\"{raw}\" 不是整数，已忽略。");
        return null;
    }

    /// <summary>
    /// 追加一行「本次调用记录」：解析到的 -env:UserInstallation + 当前 PID（外加时间戳与完整参数）。
    /// 测试据此证明两件事：每次调用都拿到独立的用户 profile；被杀的进程确实不在了。
    /// </summary>
    private static void AppendProfileLog(string? profileUrl)
    {
        string? configuredPath = Environment.GetEnvironmentVariable("RENAMEPRO_STUB_PROFILE_LOG");
        if (string.IsNullOrWhiteSpace(configuredPath)) return;

        string line = string.Join('\t',
            DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            "pid=" + Environment.ProcessId,
            "userInstallation=" + (string.IsNullOrEmpty(profileUrl) ? "(none)" : profileUrl),
            "args=" + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));

        try
        {
            string fullPath = Path.GetFullPath(configuredPath);

            // 规范要求：父目录不存在就创建（测试脚本可以先设环境变量、不预建目录）。
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // UTF-8 追加：并发调用时每个进程只会追加自己那一整行。
                    // 只在新建文件时写 BOM（诊断文件需要被 PowerShell 5.1 的 Get-Content 正确解码；
                    // 若每次追加都写 BOM，文件中间会出现多余的 BOM 字节）。
                    bool isNewFile = !File.Exists(fullPath);
                    File.AppendAllText(fullPath,
                        (isNewFile ? "\uFEFF" : string.Empty) + line + Environment.NewLine,
                        Utf8NoBom);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    // Windows 上并发追加同一文件会短暂拒绝共享，重试即可。
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            // 日志失败不能影响转换本身（真实 LO 的 profile 问题也只打警告），但要让人看见。
            Console.Error.WriteLine(
                $"警告：无法写入 RENAMEPRO_STUB_PROFILE_LOG（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    // ——————————————————————————————————————————————————————————————
    // 杂项
    // ——————————————————————————————————————————————————————————————

    /// <summary>参数级失败：stderr 写中文可操作原因，返回给定退出码。</summary>
    private static int Fail(int exitCode, string chineseMessage)
    {
        Console.Error.WriteLine(chineseMessage);
        return exitCode;
    }

    private static void PrintUsage()
    {
        string[] lines =
        {
            "RenamePro test double: stub soffice (emulates the LibreOffice headless CLI).",
            "用法 / Usage:",
            "  soffice.exe --version",
            "  soffice.exe --help",
            "  soffice.exe [--headless --norestore --nolockcheck --nodefault --nofirststartwizard --nologo]",
            "              -env:UserInstallation=file:///C:/temp/profile",
            "              --convert-to \"<EXT>[:<FilterName>[:<FilterOptions>]]\" --outdir \"<DIR>\" \"<SRC>\"",
            "",
            "行为 / Behaviour:",
            "  * 产物名 = 源文件基名 + 目标扩展名，写在 --outdir 下（与真实 LibreOffice 一致，调用方自行对账）",
            "  * 成功退出码 0；输入文件不存在 / 缺少 --outdir 退出码 1（stderr 中文提示）",
            "  * pdf -> docx 退出码 3（Error: no filter for pdf -> docx）",
            "  * 目标扩展名未知：写纯文本占位文件 + stderr 警告 + 退出码 0（用于测试「产物不可验证必须拒绝」）",
            "  * 未知开关一律静默忽略；-env:UserInstallation 可出现在任意位置",
            "",
            "测试钩子 / Env hooks:",
            "  RENAMEPRO_STUB_DELAY_MS=N      写出产物前先睡 N 毫秒",
            "  RENAMEPRO_STUB_HANG=1          解析完参数后无限挂起（不写产物）",
            "  RENAMEPRO_STUB_NO_OUTPUT=1     退出码 0 但不写任何产物（真实 LO 的假成功）",
            "  RENAMEPRO_STUB_STDERR=<text>   退出前把这行文本写到 stderr",
            "  RENAMEPRO_STUB_EXIT=<code>     强制最终退出码（成功路径）",
            "  RENAMEPRO_STUB_PROFILE_LOG=<path>  每次调用追加一行 profile + PID"
        };
        foreach (string line in lines) Console.Out.WriteLine(line);
    }

    /// <summary>把 stdout/stderr 固定为 UTF-8（无 BOM），保证中文提示在任何宿主下都可读。</summary>
    private static void UseUtf8Writers()
    {
        try
        {
            Console.OutputEncoding = Utf8NoBom;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // 没有真实控制台句柄时会失败，忽略：下面直接换写入器即可。
        }

        try
        {
            // AutoFlush：即使调用方随后把进程杀掉，已经写出的内容也不会留在缓冲区里丢掉。
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // 标准流被关闭等极端情况：保持默认写入器，不要因此让桩崩掉。
        }
    }

    private static void Ascii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void Utf8(Stream stream, string text)
    {
        byte[] bytes = Utf8NoBom.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>按行切分（同时兼容 CRLF 与 LF）。</summary>
    private static string[] LinesOf(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static string XmlEscape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
