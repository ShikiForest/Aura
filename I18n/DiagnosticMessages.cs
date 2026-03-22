using System.Collections.Generic;

namespace AuraLang.I18n;

/// <summary>
/// Diagnostic message catalog for all compiler phases.
/// Codes: AUR#### (semantic), CG#### (codegen), LWR#### / AURLW#### (lowering).
/// </summary>
public static class DiagnosticMessages
{
    private static readonly Dictionary<(string Code, AuraLocale Locale), string> _table = new();

    static DiagnosticMessages()
    {
        // ── AUR1xxx: Declaration / duplicate ────────────────────────
        Add("AUR1010",
            "Duplicate declaration: {0}",
            "宣言の重複: {0}",
            "重复声明：{0}");

        Add("AUR1011",
            "Duplicate member: {0}.{1}",
            "メンバーの重複: {0}.{1}",
            "重复成员：{0}.{1}");

        Add("AUR1012",
            "Nested {0} declaration is not handled by the v1 semantic checker (can be ignored).",
            "v1 意味解析器はネストされた {0} 宣言を未処理です（無視可能）。",
            "v1 语义检查器未处理嵌套 {0} 声明（可忽略）");

        Add("AUR1020.param",
            "Duplicate parameter: {0}",
            "パラメータの重複: {0}",
            "重复定义参数：{0}");

        Add("AUR1020.var",
            "Duplicate variable: {0}",
            "変数の重複: {0}",
            "重复定义变量：{0}");

        Add("AUR1020.lambda",
            "Duplicate lambda parameter: {0}",
            "ラムダパラメータの重複: {0}",
            "lambda 参数重复：{0}");

        // ── AUR2xxx: Expression / type checks ──────────────────────
        Add("AUR2101",
            "Cannot assign to immutable let binding: {0}",
            "不変バインディング let には代入できません: {0}",
            "不可变绑定 let 不允许赋值：{0}");

        Add("AUR2104",
            "let bindings must be initialized at declaration.",
            "let は宣言時に初期化が必要です。",
            "let 必须在声明时初始化");

        Add("AUR2220.await_using",
            "await using can only be used inside an async function.",
            "await using は async 関数内でのみ使用できます。",
            "await using 只能在 async 函数中使用");

        Add("AUR2220.await",
            "await can only be used inside an async function.",
            "await は async 関数内でのみ使用できます。",
            "await 只能在 async 函数中使用");

        Add("AUR2230",
            "State function group '{0}': all parameter lists must match.",
            "状態関数グループ '{0}': パラメータリストが一致する必要があります。",
            "状态函数组 {0} 参数列表必须一致");

        Add("AUR2231.format",
            "State function return must be of form Enum.Value: got {0}",
            "状態関数の戻り値は Enum.Value 形式でなければなりません: 実際は {0}",
            "状态函数返回必须形如 Enum.Value：实际 {0}");

        Add("AUR2231.enum",
            "State function group '{0}' must use the same state enum type.",
            "状態関数グループ '{0}' は同じ状態列挙型を使用する必要があります。",
            "状态函数组 {0} 必须使用同一状态枚举类型");

        Add("AUR2232",
            "Duplicate implementation for state {0}.{1}: {2}",
            "状態 {0}.{1} の実装が重複しています: {2}",
            "状态 {0}.{1} 的实现重复：{2}");

        Add("AUR2233",
            "State function group '{0}' does not cover all states: missing {1}",
            "状態関数グループ '{0}' が全状態を網羅していません: 不足 {1}",
            "状态函数组 {0} 未覆盖所有状态：缺少 {1}");

        Add("AUR2302",
            "??= can only be used on nullable targets: {0}",
            "??= はNull許容型の対象にのみ使用可能です: {0}",
            "??= 只能用于可空目标：{0}");

        Add("AUR2310",
            "Left side of ?? must be a nullable expression: got {0}",
            "?? の左辺はNull許容式でなければなりません: 実際は {0}",
            "?? 左侧必须是可空表达式：实际 {0}");

        Add("AUR2321",
            "Incompatible branch types in conditional expression: {0} and {1}",
            "条件式の分岐型が互換性がありません: {0} と {1}",
            "条件表达式分支类型不兼容：{0} 与 {1}");

        Add("AUR2402",
            "Placeholder '_' can only be used in a pipe stage's right-hand arguments.",
            "プレースホルダー '_' はパイプステージの右辺引数内でのみ使用できます。",
            "占位符 '_' 只能用于管道调用的右侧 stage 参数中");

        Add("AUR2410.error",
            "Exception guard handler must be of form (Exception)->T (lambda must have exactly 1 parameter).",
            "例外ガードハンドラーは (Exception)->T 形式でなければなりません（ラムダは引数1つ）。",
            "异常守护 handler 必须是 (Exception)->T 形式（lambda 参数个数必须为 1）");

        Add("AUR2410.warn",
            "Exception guard handler should be a (Exception)->T lambda (v1 type check incomplete).",
            "例外ガードハンドラーは (Exception)->T ラムダの使用を推奨します（v1型検査は不完全）。",
            "异常守护 handler 建议使用 (Exception)->T lambda（v1 未完全类型检查）");

        Add("AUR2510",
            "Switch expression branch result types cannot be reliably merged (v1 type inference is weak).",
            "switch 式の分岐結果型を確実にマージできません（v1型推論は弱い）。",
            "switch expression 分支结果类型无法可靠合并（v1 类型推导较弱）");

        Add("AUR2511",
            "Switch expression must be exhaustive: add a '_' branch.",
            "switch 式は網羅的でなければなりません: '_' 分岐を追加してください。",
            "switch expression 必须穷尽：请添加 '_' 分支");

        Add("AUR2601",
            "{0} (got {1})",
            "{0}（実際は {1}）",
            "{0}（实际 {1}）");

        Add("AUR2620",
            "using resource must implement IDisposable: {0}",
            "using リソースは IDisposable を実装する必要があります: {0}",
            "using 资源必须实现 IDisposable：{0}");

        Add("AUR2621",
            "await using resource must implement IAsyncDisposable: {0}",
            "await using リソースは IAsyncDisposable を実装する必要があります: {0}",
            "await using 资源必须实现 IAsyncDisposable：{0}");

        Add("AUR2622",
            "IDisposable/IAsyncDisposable constraint on using resource cannot be fully verified in v1 (can be ignored or improve type resolution).",
            "using リソースの IDisposable/IAsyncDisposable 制約は v1 で完全に検証できません（無視可能、型解決の改善で対応）。",
            "using 资源的 IDisposable/IAsyncDisposable 约束在 v1 中无法完全判定（可忽略或完善类型解析）");

        Add("AUR2630",
            "catch type must be Exception or a derived type: got {0}",
            "catch 型は Exception またはその派生型でなければなりません: 実際は {0}",
            "catch 类型必须是 Exception 或派生：实际 {0}");

        // ── AUR4xxx: Architecture constraints ──────────────────────
        Add("AUR4001",
            "Public fields are forbidden: {0}.{1} (use pub property instead).",
            "公開フィールドは禁止されています: {0}.{1}（pub property を使用してください）。",
            "禁止公开字段：{0}.{1}（请改用 pub property）");

        Add("AUR4002",
            "pub property type not on whitelist: {0}.{1} : {2}",
            "pub property の型がホワイトリストにありません: {0}.{1} : {2}",
            "pub property 类型不符合白名单：{0}.{1} : {2}");

        Add("AUR4010",
            "Trait member not implemented: {0}.{1} (required signature: {2})",
            "トレイトメンバーが未実装です: {0}.{1}（必要なシグネチャ: {2}）",
            "未实现 trait 成员：{0}.{1}（需要签名 {2}）");

        Add("AUR4020",
            "struct cannot inherit from class: {0}",
            "struct は class を継承できません: {0}",
            "struct 不能继承 class：{0}");

        Add("AUR4031.kind",
            "Cannot use new on {0}: {1}",
            "{0} に対して new は使用できません: {1}",
            "不能对 {0} 使用 new：{1}");

        Add("AUR4031.noarg",
            "No-argument new() is forbidden in Aura: instantiation must use a builder (pass at least one builder argument).",
            "引数なし new() は Aura では禁止されています: ビルダーを使用してインスタンス化してください（最低1つのビルダー引数が必要）。",
            "Aura 禁止无参 new()：实例化必须通过 builder（至少传入 builder 参数）");

        Add("AUR4032",
            "Normal 'new Type(...)' is forbidden in Aura. Use 'new(builder)' with an IBuilder instance, or use CLRExternalTypeBuilder<T> for CLR types.",
            "通常の 'new Type(...)' は Aura では禁止されています。IBuilder インスタンスによる 'new(builder)' を使用するか、CLR 型には CLRExternalTypeBuilder<T> を使用してください。",
            "Aura 禁止普通 'new Type(...)'。请使用 'new(builder)' 配合 IBuilder 实例，或使用 CLRExternalTypeBuilder<T> 处理 CLR 类型。");

        Add("AUR4050",
            "User-defined constructors are not allowed in Aura. Remove 'fn new(...)' from '{0}'. Use [BuildMe] with a builder class instead.",
            "Aura ではユーザー定義コンストラクタは禁止されています。'{0}' から 'fn new(...)' を削除し、[BuildMe] とビルダークラスを使用してください。",
            "Aura 禁止用户定义构造函数。请从 '{0}' 中删除 'fn new(...)'，改用 [BuildMe] + builder 类。");

        Add("AUR4100.target_type",
            "window target type must be class/struct: got {0}",
            "window の対象型は class/struct でなければなりません: 実際は {0}",
            "window 目标类型必须是 class/struct：实际为 {0}");

        Add("AUR4100.resolve",
            "Cannot resolve window target type: {0}",
            "window の対象型を解決できません: {0}",
            "无法解析 window 目标类型：{0}");

        Add("AUR4100.member",
            "window member '{0}' is not a pub property of target type '{1}'.",
            "window メンバー '{0}' は対象型 '{1}' の pub property に存在しません。",
            "window 成员 {0} 不存在于目标类型 {1} 的 pub property 中");

        Add("AUR4101",
            "window member type mismatch: target {0}.{1} is {2}, window declares {3}",
            "window メンバーの型が不一致: 対象 {0}.{1} は {2}、window 宣言は {3}",
            "window 成员类型不匹配：目标 {0}.{1} 为 {2}，window 声明为 {3}");

        Add("AUR4200",
            "self decode function must have exactly one parameter (permission enum type), found {0}.",
            "self decode 関数はパラメータが1つ（権限列挙型）である必要があります。{0} 個見つかりました。",
            "self decode 函数必须恰好有一个参数（权限枚举类型），实际为 {0}。");

        Add("AUR4201",
            "self decode function must return windowof<{0}>, not {1}.",
            "self decode 関数は windowof<{0}> を返す必要があります。{1} ではありません。",
            "self decode 函数必须返回 windowof<{0}>，而非 {1}。");

        Add("AUR4300.param",
            "'item' cannot be used as a parameter name (item is reserved for predicate indexers).",
            "'item' はパラメータ名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作参数名（item 仅用于谓词索引器）");

        Add("AUR4300.loop",
            "'item' cannot be used as a loop variable name (item is reserved for predicate indexers).",
            "'item' はループ変数名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作循环变量名（item 仅用于谓词索引器）");

        Add("AUR4300.using",
            "'item' cannot be used as a using variable name (item is reserved for predicate indexers).",
            "'item' は using 変数名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作 using 变量名（item 仅用于谓词索引器）");

        Add("AUR4300.catch",
            "'item' cannot be used as a catch variable name (item is reserved for predicate indexers).",
            "'item' は catch 変数名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作 catch 变量名（item 仅用于谓词索引器）");

        Add("AUR4300.pattern",
            "'item' cannot be used as a pattern variable (item is reserved for predicate indexers).",
            "'item' はパターン変数名に使用できません（item は述語インデクサー専用）。",
            "pattern 变量不允许叫 item（item 仅用于谓词索引器）");

        Add("AUR4300.var",
            "'item' cannot be used as a variable name (item is reserved for predicate indexers).",
            "'item' は変数名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作变量名（item 仅用于谓词索引器）");

        Add("AUR4300.name",
            "'item' can only be used in predicate indexers: collection[item ...]",
            "'item' は述語インデクサーでのみ使用可能: collection[item ...]",
            "item 只能用于谓词索引器：collection[item ...]");

        Add("AUR4300.lambda",
            "'item' cannot be used as a lambda parameter name (item is reserved for predicate indexers).",
            "'item' はラムダパラメータ名に使用できません（item は述語インデクサー専用）。",
            "禁止把 item 用作 lambda 参数名（item 仅用于谓词索引器）");

        Add("AUR4400",
            "Aura does not support bitwise operators: {0}",
            "Aura はビット演算子をサポートしていません: {0}",
            "Aura 不支持位运算符：{0}");

        // ── AUR5xxx: Deprecation warnings ────────────────────────────
        Add("AUR5001",
            "try/catch is obsoleted, use ~ instead.",
            "try/catch は非推奨です。代わりに ~ を使用してください。",
            "try/catch 已弃用，请使用 ~ 代替。");

        // ── CG1xxx: Type resolution (codegen) ──────────────────────
        Add("CG1001",
            "Unknown type '{0}'",
            "不明な型 '{0}'",
            "未知类型 '{0}'");

        Add("CG1002",
            "Duplicate top-level function name '{0}' (overloads not supported yet). Keeping the first one.",
            "トップレベル関数名 '{0}' が重複しています（オーバーロード未サポート）。最初のものを保持します。",
            "顶层函数名 '{0}' 重复（暂不支持重载）。保留第一个。");

        Add("CG1101",
            "Parameter '{0}' must have an explicit type in codegen v3.",
            "パラメータ '{0}' には codegen v3 で明示的な型が必要です。",
            "参数 '{0}' 在 codegen v3 中必须有显式类型。");

        Add("CG1102",
            "Unknown return spec; treating as void.",
            "不明な戻り値指定。void として扱います。",
            "未知返回值规范；视为 void。");

        Add("CG1103",
            "Generic constraints (where-clauses) are not emitted yet; constraints will be ignored for '{0}'.",
            "ジェネリック制約（where句）はまだ出力されません。'{0}' の制約は無視されます。",
            "泛型约束（where 子句）尚未生成；'{0}' 的约束将被忽略。");

        Add("CG1104",
            "Generic user-defined types are not fully supported yet; '{0}<...>' will be treated as non-generic.",
            "ジェネリックなユーザー定義型はまだ完全にサポートされていません。'{0}<...>' は非ジェネリックとして扱われます。",
            "泛型用户定义类型尚未完全支持；'{0}<...>' 将被视为非泛型。");

        // ── CG2xxx: Function / statement (codegen) ─────────────────
        Add("CG2000",
            "Unknown function body form for {0}.",
            "関数 {0} の本体形式が不明です。",
            "函数 {0} 的函数体形式未知。");

        Add("CG2001.error",
            "Missing return value for non-void method '{0}'.",
            "非 void メソッド '{0}' に戻り値がありません。",
            "非 void 方法 '{0}' 缺少返回值。");

        Add("CG2001.warn",
            "Cannot infer type for expression '{0}'; defaulting to object.",
            "式 '{0}' の型を推論できません。object をデフォルトとします。",
            "无法推断表达式 '{0}' 的类型；默认为 object。");

        Add("CG2101",
            "Return statement missing value in non-void method '{0}'.",
            "非 void メソッド '{0}' の return 文に値がありません。",
            "非 void 方法 '{0}' 的 return 语句缺少值。");

        Add("CG2199",
            "Statement kind not supported in codegen v3: {0}",
            "codegen v3 でサポートされていないステートメント種別: {0}",
            "codegen v3 不支持的语句类型：{0}");

        Add("CG2201",
            "Local '{0}' needs a type or initializer in codegen v3.",
            "ローカル変数 '{0}' には codegen v3 で型または初期化子が必要です。",
            "局部变量 '{0}' 在 codegen v3 中需要类型或初始化器。");

        // ── CG3xxx: Expression / operator / member (codegen) ───────
        Add("CG3000",
            "Expression kind not supported in codegen v3: {0}",
            "codegen v3 でサポートされていない式種別: {0}",
            "codegen v3 不支持的表达式类型：{0}");

        Add("CG3001",
            "Invalid int literal: {0}",
            "無効な整数リテラル: {0}",
            "无效的整数字面量：{0}");

        Add("CG3002.float",
            "Invalid float literal: {0}",
            "無効な浮動小数点リテラル: {0}",
            "无效的浮点字面量：{0}");

        Add("CG3002.name",
            "Unknown name: {0}",
            "不明な名前: {0}",
            "未知的名称：{0}");

        Add("CG3003.char",
            "Invalid char literal: {0}",
            "無効な文字リテラル: {0}",
            "无效的字符字面量：{0}");

        Add("CG3003.unary",
            "Unary operator not supported: {0}",
            "サポートされていない単項演算子: {0}",
            "不支持的一元运算符：{0}");

        Add("CG3004.literal",
            "Unknown literal kind: {0}",
            "不明なリテラル種別: {0}",
            "未知的字面量类型：{0}");

        Add("CG3004.binary",
            "Binary operator not supported: {0}",
            "サポートされていない二項演算子: {0}",
            "不支持的二元运算符：{0}");

        Add("CG3010",
            "await expects System.Threading.Tasks.Task or Task<T>, got '{0}'",
            "await は System.Threading.Tasks.Task または Task<T> を期待しますが、'{0}' が渡されました",
            "await 期望 System.Threading.Tasks.Task 或 Task<T>，实际为 '{0}'");

        Add("CG3011",
            "await cannot resolve CLR type for '{0}'",
            "await が '{0}' の CLR 型を解決できません",
            "await 无法解析 '{0}' 的 CLR 类型");

        Add("CG3012",
            "await target '{0}' has no GetAwaiter() method",
            "await 対象 '{0}' に GetAwaiter() メソッドがありません",
            "await 目标 '{0}' 没有 GetAwaiter() 方法");

        Add("CG3013",
            "await cannot resolve CLR awaiter type for '{0}'",
            "await が '{0}' の CLR awaiter 型を解決できません",
            "await 无法解析 '{0}' 的 CLR awaiter 类型");

        Add("CG3014",
            "awaiter type '{0}' has no GetResult() method",
            "awaiter 型 '{0}' に GetResult() メソッドがありません",
            "awaiter 类型 '{0}' 没有 GetResult() 方法");

        Add("CG3100.assign",
            "Assignment target not supported in codegen v3: {0}",
            "codegen v3 でサポートされていない代入先: {0}",
            "codegen v3 不支持的赋值目标：{0}");

        Add("CG3100.ctor",
            "No default constructor found for type '{0}'.",
            "型 '{0}' にデフォルトコンストラクタが見つかりません。",
            "类型 '{0}' 找不到默认构造函数。");

        Add("CG3101.coalesce",
            "??= only supported for locals in codegen v3.",
            "??= は codegen v3 でローカル変数にのみサポートされています。",
            "??= 在 codegen v3 中仅支持局部变量。");

        Add("CG3101.newexpr",
            "Cannot resolve type for new expression: {0}",
            "new 式の型を解決できません: {0}",
            "无法解析 new 表达式的类型：{0}");

        Add("CG3102",
            "??= target must be a local in codegen v3.",
            "??= の対象は codegen v3 でローカル変数でなければなりません。",
            "??= 的目标在 codegen v3 中必须是局部变量。");

        Add("CG3103",
            "??= on value types not supported in codegen v3.",
            "codegen v3 では値型に対する ??= はサポートされていません。",
            "codegen v3 不支持对值类型使用 ??=。");

        Add("CG3110",
            "Failed to emit builder-based new(builder): IBuilder interface not found in output module.",
            "ビルダーベースの new(builder) の出力に失敗しました: IBuilder インターフェースが出力モジュールに見つかりません。",
            "无法生成 builder 形式的 new(builder)：输出模块中未找到 IBuilder 接口。");

        Add("CG3200",
            "Cannot resolve member access: {0}",
            "メンバーアクセスを解決できません: {0}",
            "无法解析成员访问：{0}");

        Add("CG3201",
            "Property has no getter: {0}",
            "プロパティにゲッターがありません: {0}",
            "属性没有 getter：{0}");

        Add("CG3202",
            "Method group used as value is not supported in codegen v3: {0}",
            "メソッドグループを値として使用することは codegen v3 でサポートされていません: {0}",
            "codegen v3 不支持将方法组作为值使用：{0}");

        Add("CG3301",
            "Placeholder '_' should be removed by lowering before codegen.",
            "プレースホルダー '_' は codegen の前に lowering で除去されるべきです。",
            "占位符 '_' 应在 codegen 之前由 lowering 移除。");

        Add("CG3302",
            "Cannot invoke non-callable value: {0}",
            "呼び出し不可能な値を呼び出せません: {0}",
            "无法调用非可调用值：{0}");

        Add("CG3400",
            "TryCatchExprNode cannot be void; it must yield a value.",
            "TryCatchExprNode は void にできません。値を返す必要があります。",
            "TryCatchExprNode 不能为 void；必须产生一个值。");

        Add("CG3501",
            "Cannot resolve delegate CLR type: {0}",
            "デリゲート CLR 型を解決できません: {0}",
            "无法解析委托 CLR 类型：{0}");

        Add("CG3502",
            "Delegate type '{0}' is missing Invoke method or constructor.",
            "デリゲート型 '{0}' に Invoke メソッドまたはコンストラクタがありません。",
            "委托类型 '{0}' 缺少 Invoke 方法或构造函数。");

        Add("CG3505",
            "Captured field '{0}' used, but current method is not an instance method.",
            "キャプチャフィールド '{0}' が使用されていますが、現在のメソッドはインスタンスメソッドではありません。",
            "使用了捕获字段 '{0}'，但当前方法不是实例方法。");

        Add("CG3506",
            "Assignment to instance field '{0}' in non-instance method.",
            "非インスタンスメソッドでインスタンスフィールド '{0}' への代入。",
            "在非实例方法中对实例字段 '{0}' 赋值。");

        Add("CG3507",
            "Assignment to instance property '{0}' in non-instance method.",
            "非インスタンスメソッドでインスタンスプロパティ '{0}' への代入。",
            "在非实例方法中对实例属性 '{0}' 赋值。");

        Add("CG3508",
            "Property '{0}' has no setter.",
            "プロパティ '{0}' にセッターがありません。",
            "属性 '{0}' 没有 setter。");

        Add("CG4400",
            "Bitwise operator not supported: {0}",
            "ビット演算子はサポートされていません: {0}",
            "不支持的位运算符：{0}");

        Add("CG4099",
            "No implicit coercion rule from '{0}' to '{1}' in codegen v3.",
            "codegen v3 では '{0}' から '{1}' への暗黙的変換規則がありません。",
            "codegen v3 中没有从 '{0}' 到 '{1}' 的隐式转换规则。");

        Add("CG5001",
            "No derivable op properties found for '{0}'. derivateof will return null.",
            "'{0}' に derivable op プロパティが見つかりません。derivateof は null を返します。",
            "'{0}' 未找到可派生的 op 属性。derivateof 将返回 null。");

        Add("CG5105",
            "Could not infer generic type arguments for '{0}.{1}'; defaulting to object.",
            "'{0}.{1}' のジェネリック型引数を推論できませんでした。object をデフォルトとします。",
            "无法推断 '{0}.{1}' 的泛型类型参数；默认为 object。");

        // ── CG6xxx: Operator overloading (codegen) ─────────────────
        Add("CG6001",
            "Unsupported operator for overloading: '{0}'.",
            "オーバーロード対象としてサポートされていない演算子: '{0}'。",
            "不支持重载的运算符：'{0}'。");

        Add("CG6002",
            "Operator parameter '{0}' must have an explicit type.",
            "演算子パラメータ '{0}' には明示的な型が必要です。",
            "运算符参数 '{0}' 必须有显式类型。");

        // ── CG7xxx: BuildMe / Room (codegen) ───────────────────────
        Add("CG7001",
            "A user-defined type named 'Room' exists; skipping built-in Room runtime type emission.",
            "ユーザー定義型 'Room' が存在するため、組み込みの Room ランタイム型の出力をスキップします。",
            "存在名为 'Room' 的用户定义类型；跳过内置 Room 运行时类型生成。");

        Add("CG7100",
            "[BuildMe] registration emitted for '{0}' with builder '{1}'.",
            "[BuildMe] 登録が '{0}' に対してビルダー '{1}' で出力されました。",
            "[BuildMe] 已为 '{0}' 生成 builder '{1}' 的注册。");

        Add("CG7101",
            "[BuildMe] on '{0}' missing 'builder' argument. Skipping registration.",
            "[BuildMe] の '{0}' に 'builder' 引数がありません。登録をスキップします。",
            "[BuildMe] 在 '{0}' 上缺少 'builder' 参数。跳过注册。");

        Add("CG7102",
            "Builder type '{0}' not found for [BuildMe] registration.",
            "[BuildMe] 登録用のビルダー型 '{0}' が見つかりません。",
            "[BuildMe] 注册的 builder 类型 '{0}' 未找到。");

        // ── LWR4xxx: State functions (lowering) ────────────────────
        Add("LWR4001",
            "Invalid state spec on state function '{0}'. Expected Enum.Value.",
            "状態関数 '{0}' の状態指定が無効です。Enum.Value 形式を期待します。",
            "状态函数 '{0}' 的状态规范无效。应为 Enum.Value。");

        Add("LWR4002",
            "Could not resolve function type for op '{0}' in derivable function '{1}'.",
            "derivable 関数 '{1}' 内の演算子 '{0}' の関数型を解決できません。",
            "无法解析 derivable 函数 '{1}' 中运算符 '{0}' 的函数类型。");

        Add("LWR5001",
            "derivateof operand must be a function name.",
            "derivateof のオペランドは関数名でなければなりません。",
            "derivateof 的操作数必须是函数名。");

        // ── AURLW1xxx: Unknown nodes (lowering) ────────────────────
        Add("AURLW1000",
            "Unknown using resource node '{0}' - leaving as-is.",
            "不明な using リソースノード '{0}' - そのまま残します。",
            "未知的 using 资源节点 '{0}'——保持原样。");

        // ── AURLW2xxx: Pattern lowering ────────────────────────────
        Add("AURLW2001",
            "Variable binding (capture) in not-pattern is not supported yet. Move the binding outside the not-pattern, or simplify the pattern.",
            "not-pattern 内の変数バインディング（キャプチャ）はまだサポートされていません。バインディングを not-pattern の外に移動するか、パターンを簡略化してください。",
            "not-pattern 中暂不支持变量绑定（capture）。请将绑定移出 not-pattern，或先简化 pattern。");

        Add("AURLW2002",
            "Variable binding (capture) in or-pattern is not supported yet because different branches may result in inconsistent variable assignment. Use patterns without binding, or split into if/switch statements.",
            "or-pattern 内の変数バインディング（キャプチャ）はまだサポートされていません。異なる分岐で変数代入が不整合になる可能性があるためです。バインディングなしのパターンを使用するか、if/switch 文に分割してください。",
            "or-pattern 中暂不支持变量绑定（capture）。因为不同分支可能导致变量赋值不一致。请改用不绑定的 pattern，或拆成 if/switch 语句。");

        Add("AURLW2003",
            "list-pattern lowering is not implemented yet. Future: lower to length check + per-element matching.",
            "list-pattern の lowering はまだ実装されていません。将来的には長さチェック+要素ごとのマッチングに lowering されます。",
            "list-pattern 暂未实现 lowering。后续可以 lowering 成长度检查 + 元素逐个匹配。");

        Add("AURLW2010",
            "Duplicate pattern variable binding: {0} (declared more than once in the same pattern)",
            "パターン変数バインディングの重複: {0}（同一パターン内で複数回宣言）",
            "pattern 变量绑定重复：{0}（在同一 pattern 中重复声明）");

        Add("AURLW2099",
            "Unsupported pattern node for lowering: {0}",
            "lowering でサポートされていないパターンノード: {0}",
            "lowering 不支持的 pattern 节点：{0}");

        Add("AURLW2100",
            "TypePattern with qualified name (e.g., Enum.Member) in pattern is being treated as constant matching. Consider classifying it as ConstantPattern in the symbol binding phase.",
            "パターン内の TypePattern が修飾名（例: Enum.Member）を使用しています。定数マッチとして処理されます。シンボルバインディングフェーズで ConstantPattern に分類することを推奨します。",
            "pattern 中的 TypePattern 使用了带 '.' 的限定名（例如 Enum.Member）。本次 lowering 将其按常量匹配处理。建议后续在符号绑定阶段将其归类为 ConstantPattern。");

        // ── AURLW3xxx: Async lowering ──────────────────────────────
        Add("AURLW3001",
            "await in an if-condition is not supported by the non-blocking async lowering yet; falling back to blocking await for this function.",
            "if 条件内の await はノンブロッキング async lowering でまだサポートされていません。この関数ではブロッキング await にフォールバックします。",
            "非阻塞 async lowering 尚不支持 if 条件中的 await；此函数将回退到阻塞 await。");

        Add("AURLW3002",
            "await must appear as 'return await expr;' (not nested inside a larger expression) for non-blocking async lowering; falling back to blocking await for this function.",
            "ノンブロッキング async lowering では await は 'return await expr;' の形式で記述する必要があります（より大きな式の中にネストしないでください）。この関数ではブロッキング await にフォールバックします。",
            "非阻塞 async lowering 要求 await 以 'return await expr;' 形式出现（不能嵌套在更大的表达式中）；此函数将回退到阻塞 await。");

        Add("AURLW3003",
            "await must appear as the whole initializer ('let x = await expr;') for non-blocking async lowering; falling back to blocking await for this function.",
            "ノンブロッキング async lowering では await は初期化子全体（'let x = await expr;'）として記述する必要があります。この関数ではブロッキング await にフォールバックします。",
            "非阻塞 async lowering 要求 await 作为完整初始化器出现（'let x = await expr;'）；此函数将回退到阻塞 await。");

        Add("AURLW3004",
            "await must appear as a standalone statement ('await expr;') or assignment ('x = await expr;') for non-blocking async lowering; falling back to blocking await for this function.",
            "ノンブロッキング async lowering では await は独立文（'await expr;'）または代入（'x = await expr;'）として記述する必要があります。この関数ではブロッキング await にフォールバックします。",
            "非阻塞 async lowering 要求 await 作为独立语句（'await expr;'）或赋值（'x = await expr;'）出现；此函数将回退到阻塞 await。");

        Add("AURLW3005",
            "await in '{0}' is not supported by the non-blocking async lowering yet; falling back to blocking await for this function.",
            "'{0}' 内の await はノンブロッキング async lowering でまだサポートされていません。この関数ではブロッキング await にフォールバックします。",
            "非阻塞 async lowering 尚不支持 '{0}' 中的 await；此函数将回退到阻塞 await。");

        Add("AURLW3006",
            "await in if-condition is not supported by non-blocking async lowering; treating the condition as blocking.",
            "if 条件内の await はノンブロッキング async lowering でサポートされていません。条件をブロッキングとして処理します。",
            "非阻塞 async lowering 不支持 if 条件中的 await；将条件视为阻塞处理。");
    }

    public static string Get(string code, AuraLocale locale, params object[] args)
    {
        if (_table.TryGetValue((code, locale), out var fmt))
            return args.Length > 0 ? string.Format(fmt, args) : fmt;

        // Fallback: try English
        if (locale != AuraLocale.En && _table.TryGetValue((code, AuraLocale.En), out fmt))
            return args.Length > 0 ? string.Format(fmt, args) : fmt;

        // Last resort: return code itself
        return code;
    }

    private static void Add(string code, string en, string ja, string zh)
    {
        _table[(code, AuraLocale.En)] = en;
        _table[(code, AuraLocale.Ja)] = ja;
        _table[(code, AuraLocale.Zh)] = zh;
    }
}
