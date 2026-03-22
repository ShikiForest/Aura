using System.Collections.Generic;

namespace AuraLang.I18n;

/// <summary>
/// CLI message catalog for all user-facing CLI output.
/// Keys are snake_case identifiers.
/// </summary>
public static class CliMessages
{
    private static readonly Dictionary<(string Key, AuraLocale Locale), string> _table = new();

    static CliMessages()
    {
        // ── General ────────────────────────────────────────────────
        Add("file_not_found",
            "File not found: {0}",
            "ファイルが見つかりません: {0}",
            "文件未找到：{0}");

        Add("no_source_file",
            "No source file specified.",
            "ソースファイルが指定されていません。",
            "未指定源文件。");

        Add("unknown_subcommand",
            "Unknown subcommand '{0}'. Run 'aura --help' for usage.",
            "不明なサブコマンド '{0}'。'aura --help' で使い方を確認してください。",
            "未知子命令 '{0}'。运行 'aura --help' 查看用法。");

        Add("unknown_option",
            "Unknown option '{0}'. Run 'aura {1} --help' for usage.",
            "不明なオプション '{0}'。'aura {1} --help' で使い方を確認してください。",
            "未知选项 '{0}'。运行 'aura {1} --help' 查看用法。");

        Add("unexpected_arg",
            "Unexpected argument '{0}'. Only one source file is allowed.",
            "予期しない引数 '{0}'。ソースファイルは1つのみ指定できます。",
            "意外参数 '{0}'。只允许一个源文件。");

        Add("out_requires_path",
            "--out requires a path argument.",
            "--out にはパス引数が必要です。",
            "--out 需要路径参数。");

        Add("name_requires_arg",
            "--name requires a name argument.",
            "--name には名前引数が必要です。",
            "--name 需要名称参数。");

        Add("target_requires_tfm",
            "--target requires a TFM argument.",
            "--target には TFM 引数が必要です。",
            "--target 需要 TFM 参数。");

        // ── Phase labels ───────────────────────────────────────────
        Add("phase_parsing",
            "Parsing",
            "解析",
            "解析");

        Add("phase_semantic",
            "Semantic analysis",
            "意味解析",
            "语义分析");

        Add("phase_lowering",
            "Lowering",
            "ローワリング",
            "降级变换");

        Add("phase_codegen",
            "Code generation",
            "コード生成",
            "代码生成");

        Add("phase_packaging",
            "EXE packaging",
            "EXE パッケージング",
            "EXE 打包");

        Add("phase_publishing",
            "Publishing",
            "パブリッシュ",
            "发布");

        // ── Phase results ──────────────────────────────────────────
        Add("parse_failed",
            "Parse failed",
            "解析に失敗しました",
            "解析失败");

        Add("ast_built",
            "AST built",
            "AST 構築完了",
            "AST 构建完成");

        Add("semantic_failed",
            "Semantic analysis failed",
            "意味解析に失敗しました",
            "语义分析失败");

        Add("semantic_found_errors",
            "Semantic analysis found errors",
            "意味解析でエラーが見つかりました",
            "语义分析发现错误");

        Add("lowering_failed",
            "Lowering failed",
            "ローワリングに失敗しました",
            "降级变换失败");

        Add("lowering_skipped",
            "Lowering skipped (--no-lower)",
            "ローワリングをスキップしました (--no-lower)",
            "跳过降级变换 (--no-lower)");

        Add("nolower_warning",
            "WARNING: --no-lower is a debug option. Code generation expects lowered AST and may produce incorrect output.",
            "警告: --no-lower はデバッグ用オプションです。コード生成は lowering 済み AST を前提としており、不正な出力が生成される可能性があります。",
            "警告：--no-lower 是调试选项。代码生成假定 AST 已经过降级变换，可能产生不正确的输出。");

        Add("codegen_failed",
            "Code generation failed",
            "コード生成に失敗しました",
            "代码生成失败");

        Add("n_diagnostics",
            "{0} diagnostic(s)",
            "{0} 件の診断",
            "{0} 个诊断");

        Add("dll_written",
            "DLL written: {0}  ({1} bytes)",
            "DLL 出力: {0}  ({1} バイト)",
            "DLL 已写入：{0}（{1} 字节）");

        Add("pdb_written",
            "PDB written: {0}  ({1} bytes)",
            "PDB 出力: {0}  ({1} バイト)",
            "PDB 已写入：{0}（{1} 字节）");

        // ── Run command ────────────────────────────────────────────
        Add("host_project_failed",
            "Failed to create host project: {0}",
            "ホストプロジェクトの作成に失敗しました: {0}",
            "创建主机项目失败：{0}");

        Add("publish_failed",
            "dotnet publish exited with code {0}",
            "dotnet publish がコード {0} で終了しました",
            "dotnet publish 以代码 {0} 退出");

        Add("exe_not_found",
            "EXE not found after publish: {0}",
            "パブリッシュ後に EXE が見つかりません: {0}",
            "发布后未找到 EXE：{0}");

        Add("running",
            "Running: {0}",
            "実行中: {0}",
            "运行：{0}");

        Add("process_exited",
            "Process exited with code {0}",
            "プロセスがコード {0} で終了しました",
            "进程以代码 {0} 退出");

        // ── Verbose labels ─────────────────────────────────────────
        Add("label_source",
            "source",
            "ソース",
            "源文件");

        Add("label_output",
            "output",
            "出力先",
            "输出");

        Add("label_assembly",
            "assembly name",
            "アセンブリ名",
            "程序集名称");

        Add("label_host_project",
            "Host project: {0}",
            "ホストプロジェクト: {0}",
            "主机项目：{0}");

        // ── Severity labels ────────────────────────────────────────
        Add("severity_error",
            "error",
            "エラー",
            "错误");

        Add("severity_warning",
            "warning",
            "警告",
            "警告");

        Add("severity_info",
            "info",
            "情報",
            "信息");

        // ── Summary ────────────────────────────────────────────────
        Add("summary_errors",
            "{0} error(s)",
            "{0} 件のエラー",
            "{0} 个错误");

        Add("summary_warnings",
            "{0} warning(s)",
            "{0} 件の警告",
            "{0} 个警告");

        Add("summary_total_ms",
            "{0} ms total",
            "合計 {0} ms",
            "共计 {0} ms");

        // ── Help text ──────────────────────────────────────────────
        Add("help_main",
            """
            Aura compiler  —  usage:
              aura <subcommand> [options]

            SUBCOMMANDS:
              compile <file.aura>   Compile source to a .dll
              run     <file.aura>   Compile, package, and execute
              check   <file.aura>   Parse and type-check only (no output)
              version               Show version

            GLOBAL OPTIONS:
              --lang <en|ja|zh>     Set message language
              -h, --help            Show help for a subcommand
              -V, --version         Show version

            Run 'aura <subcommand> --help' for subcommand-specific options.
            """,
            """
            Aura コンパイラ — 使い方:
              aura <サブコマンド> [オプション]

            サブコマンド:
              compile <file.aura>   ソースを .dll にコンパイル
              run     <file.aura>   コンパイル、パッケージング、実行
              check   <file.aura>   解析と型チェックのみ（出力なし）
              version               バージョン表示

            グローバルオプション:
              --lang <en|ja|zh>     メッセージ言語の設定
              -h, --help            サブコマンドのヘルプ表示
              -V, --version         バージョン表示

            'aura <サブコマンド> --help' でサブコマンドのオプションを確認できます。
            """,
            """
            Aura 编译器 — 用法：
              aura <子命令> [选项]

            子命令：
              compile <file.aura>   将源代码编译为 .dll
              run     <file.aura>   编译、打包并执行
              check   <file.aura>   仅解析和类型检查（无输出）
              version               显示版本

            全局选项：
              --lang <en|ja|zh>     设置消息语言
              -h, --help            显示子命令帮助
              -V, --version         显示版本

            运行 'aura <子命令> --help' 查看子命令选项。
            """);

        Add("help_compile",
            """
            aura compile <file.aura> [options]

            Compiles an Aura source file to a .NET DLL.

            OPTIONS:
              -o, --out <path>      Output DLL path
                                    (default: <file>.dll next to the source)
              --name <name>         Assembly name
                                    (default: source filename without extension)
              --lang <en|ja|zh>     Set message language
              -v, --verbose         Verbose output: show all diagnostics + timings
              --no-lower            Skip the lowering phase (debug / stress-test)
              -h, --help            Show this help
            """,
            """
            aura compile <file.aura> [オプション]

            Aura ソースファイルを .NET DLL にコンパイルします。

            オプション:
              -o, --out <path>      出力 DLL パス
                                    （デフォルト: ソースと同じ場所に <file>.dll）
              --name <name>         アセンブリ名
                                    （デフォルト: 拡張子なしのファイル名）
              --lang <en|ja|zh>     メッセージ言語の設定
              -v, --verbose         詳細出力: 全診断 + タイミング表示
              --no-lower            ローワリングフェーズをスキップ（デバッグ用）
              -h, --help            このヘルプを表示
            """,
            """
            aura compile <file.aura> [选项]

            将 Aura 源文件编译为 .NET DLL。

            选项：
              -o, --out <path>      输出 DLL 路径
                                    （默认：源文件旁的 <file>.dll）
              --name <name>         程序集名称
                                    （默认：不含扩展名的文件名）
              --lang <en|ja|zh>     设置消息语言
              -v, --verbose         详细输出：显示所有诊断 + 耗时
              --no-lower            跳过降级变换阶段（调试用）
              -h, --help            显示此帮助
            """);

        Add("help_run",
            """
            aura run <file.aura> [options]

            Compiles an Aura source file, packages it as an executable,
            and runs it in-process.

            OPTIONS:
              -o, --out <path>      Intermediate DLL path
              --name <name>         Assembly name
              --lang <en|ja|zh>     Set message language
              -v, --verbose         Verbose output
              --no-lower            Skip the lowering phase (debug)
              --target <tfm>        Target framework moniker  (default: net10.0)
              --self-contained      Produce a self-contained EXE
              -h, --help            Show this help
            """,
            """
            aura run <file.aura> [オプション]

            Aura ソースファイルをコンパイルし、実行可能ファイルとしてパッケージングして実行します。

            オプション:
              -o, --out <path>      中間 DLL パス
              --name <name>         アセンブリ名
              --lang <en|ja|zh>     メッセージ言語の設定
              -v, --verbose         詳細出力
              --no-lower            ローワリングフェーズをスキップ（デバッグ用）
              --target <tfm>        ターゲットフレームワーク（デフォルト: net10.0）
              --self-contained      自己完結型 EXE の生成
              -h, --help            このヘルプを表示
            """,
            """
            aura run <file.aura> [选项]

            编译 Aura 源文件，打包为可执行文件并运行。

            选项：
              -o, --out <path>      中间 DLL 路径
              --name <name>         程序集名称
              --lang <en|ja|zh>     设置消息语言
              -v, --verbose         详细输出
              --no-lower            跳过降级变换阶段（调试用）
              --target <tfm>        目标框架名（默认：net10.0）
              --self-contained      生成独立 EXE
              -h, --help            显示此帮助
            """);

        Add("help_check",
            """
            aura check <file.aura> [options]

            Parses and type-checks a source file without producing any output.
            Exits 0 if there are no errors, 1 otherwise.

            OPTIONS:
              --lang <en|ja|zh>     Set message language
              -v, --verbose         Verbose output
              -h, --help            Show this help
            """,
            """
            aura check <file.aura> [オプション]

            ソースファイルの解析と型チェックのみ行い、出力は生成しません。
            エラーがなければ 0、あれば 1 で終了します。

            オプション:
              --lang <en|ja|zh>     メッセージ言語の設定
              -v, --verbose         詳細出力
              -h, --help            このヘルプを表示
            """,
            """
            aura check <file.aura> [选项]

            仅解析和类型检查源文件，不生成任何输出。
            无错误时返回 0，否则返回 1。

            选项：
              --lang <en|ja|zh>     设置消息语言
              -v, --verbose         详细输出
              -h, --help            显示此帮助
            """);
    }

    public static string Get(string key, AuraLocale locale, params object[] args)
    {
        if (_table.TryGetValue((key, locale), out var fmt))
            return args.Length > 0 ? string.Format(fmt, args) : fmt;

        // Fallback: try English
        if (locale != AuraLocale.En && _table.TryGetValue((key, AuraLocale.En), out fmt))
            return args.Length > 0 ? string.Format(fmt, args) : fmt;

        // Last resort: return key itself
        return key;
    }

    private static void Add(string key, string en, string ja, string zh)
    {
        _table[(key, AuraLocale.En)] = en;
        _table[(key, AuraLocale.Ja)] = ja;
        _table[(key, AuraLocale.Zh)] = zh;
    }
}
