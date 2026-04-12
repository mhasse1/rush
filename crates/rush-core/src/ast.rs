use crate::token::TokenType;

/// Base trait for AST visitors (future use).
/// All AST nodes are variants of the `Node` enum.

/// A Rush AST node.
#[derive(Debug, Clone, PartialEq)]
pub enum Node {
    /// Variable assignment: `name = expr`
    Assignment {
        name: String,
        value: Box<Node>,
    },

    /// Multiple assignment: `a, b, c = 1, 2, 3`
    MultipleAssignment {
        names: Vec<String>,
        values: Vec<Node>,
    },

    /// Compound assignment: `name += expr`
    CompoundAssignment {
        name: String,
        op: String,
        value: Box<Node>,
    },

    /// Property assignment: `receiver.property = expr`
    PropertyAssignment {
        receiver: Box<Node>,
        property: String,
        value: Box<Node>,
    },

    /// `if / elsif / else / end`
    If {
        condition: Box<Node>,
        body: Vec<Node>,
        elsifs: Vec<(Node, Vec<Node>)>,
        else_body: Option<Vec<Node>>,
    },

    /// Postfix conditional: `statement if/unless condition`
    PostfixIf {
        statement: Box<Node>,
        condition: Box<Node>,
        is_unless: bool,
    },

    /// `for variable in collection ... end`
    For {
        variable: String,
        collection: Box<Node>,
        body: Vec<Node>,
    },

    /// `parallel variable in collection ... end` — concurrent iteration
    /// Optional max_workers: `parallel(4) x in items ... end`
    /// Optional timeout: `parallel(4, 10) x in items` — 4 workers, 10s timeout
    /// fail_fast: `parallel! x in items` — stop on first error
    Parallel {
        variable: String,
        collection: Box<Node>,
        body: Vec<Node>,
        max_workers: Option<usize>,
        timeout_secs: Option<u64>,
        fail_fast: bool,
    },

    /// `orchestrate ... end` — task dependency graph with concurrent execution
    Orchestrate {
        tasks: Vec<OrchestrateTask>,
    },

    /// `while condition ... end` (also `until`, `loop`)
    While {
        condition: Box<Node>,
        body: Vec<Node>,
        is_until: bool,
    },

    /// `def name(params) ... end`
    FunctionDef {
        name: String,
        params: Vec<ParamDef>,
        body: Vec<Node>,
        is_static: bool,
        /// Raw source of the body — for mixed Rush+shell dispatch.
        raw_body: Option<String>,
    },

    /// `class Name [< Parent] ... end`
    ClassDef {
        name: String,
        parent: Option<String>,
        attributes: Vec<AttrDef>,
        constructor: Option<Box<Node>>,
        methods: Vec<Node>,
        static_methods: Vec<Node>,
    },

    /// `enum Name ... end`
    EnumDef {
        name: String,
        members: Vec<(String, Option<Box<Node>>)>,
    },

    /// `return [expr]`
    Return {
        value: Option<Box<Node>>,
    },

    /// `try/begin ... rescue ... ensure ... end`
    Try {
        body: Vec<Node>,
        rescue_var: Option<String>,
        rescue_body: Option<Vec<Node>>,
        ensure_body: Option<Vec<Node>>,
    },

    /// `case/match expr / when val ... / else ... / end`
    /// Each when has: (pattern, body, terminator)
    /// Terminator: Break (;;), Fallthrough (;&), ContinueTesting (;;&)
    Case {
        subject: Box<Node>,
        whens: Vec<(Node, Vec<Node>, CaseTerminator)>,
        else_body: Option<Vec<Node>>,
    },

    /// Loop control: `next`, `continue`, `break`
    LoopControl {
        keyword: String,
    },

    /// Variable reference (bare name)
    VariableRef {
        name: String,
    },

    /// Literal value (integer, float, string, bool, nil)
    Literal {
        value: String,
        literal_type: TokenType,
    },

    /// Regex literal: `/pattern/flags`
    RegexLiteral {
        pattern: String,
        flags: String,
    },

    /// Binary operation: `left op right`
    BinaryOp {
        left: Box<Node>,
        op: String,
        right: Box<Node>,
    },

    /// Unary operation: `not expr`, `-expr`
    UnaryOp {
        op: String,
        operand: Box<Node>,
    },

    /// Ternary: `condition ? then : else`
    Ternary {
        condition: Box<Node>,
        then_expr: Box<Node>,
        else_expr: Box<Node>,
    },

    /// Method call: `receiver.method(args) { block }`
    MethodCall {
        receiver: Box<Node>,
        method: String,
        args: Vec<Node>,
        block: Option<Box<BlockLiteral>>,
    },

    /// Function call: `name(args)`
    FunctionCall {
        name: String,
        args: Vec<Node>,
    },

    /// Property access: `receiver.property`
    PropertyAccess {
        receiver: Box<Node>,
        property: String,
    },

    /// Safe navigation: `receiver&.property`
    SafeNav {
        receiver: Box<Node>,
        member: String,
    },

    /// Interpolated string: `"hello #{name}"`
    InterpolatedString {
        parts: Vec<StringPart>,
    },

    /// Range: `start..end` or `start...end`
    Range {
        start: Box<Node>,
        end: Box<Node>,
        exclusive: bool,
    },

    /// Symbol literal: `:name`
    Symbol {
        name: String,
    },

    /// Array literal: `[1, 2, 3]`
    Array {
        elements: Vec<Node>,
    },

    /// Hash literal: `{a: 1, b: 2}`
    Hash {
        entries: Vec<(Node, Node)>,
    },

    /// Named argument: `name: value`
    NamedArg {
        name: String,
        value: Box<Node>,
    },

    /// Command substitution: `$(command)`
    CommandSub {
        command: String,
    },

    /// Static member access: `[Type]::Member(args)`
    StaticMember {
        type_name: String,
        member: String,
        args: Option<Vec<Node>>,
    },

    /// Super call: `super(args)` or `super.method(args)`
    SuperCall {
        args: Vec<Node>,
        method_name: Option<String>,
    },

    /// Platform block: `macos/win64/linux ... end` or `win32/ps/ps5 ... end`
    PlatformBlock {
        platform: String,
        body: Option<Vec<Node>>,     // parsed (macos/win64/linux)
        raw_body: Option<String>,     // raw PS (win32/ps/ps5)
        property: Option<String>,     // ".version", ".arch"
        operator: Option<String>,     // "==", ">=", etc.
        property_value: Option<String>,
    },

    /// Plugin block: `plugin.NAME ... end`
    /// Body is captured raw (not parsed as Rush) and sent to companion binary.
    PluginBlock {
        plugin_name: String,
        raw_body: String,
    },

    /// Shell passthrough
    ShellPassthrough {
        raw_command: String,
    },
}

/// A task within an orchestrate block.
#[derive(Debug, Clone, PartialEq)]
pub struct OrchestrateTask {
    pub name: String,
    pub after: Vec<String>,
    pub body: Vec<Node>,
}

/// Case statement terminator type.
#[derive(Debug, Clone, PartialEq)]
pub enum CaseTerminator {
    Break,            // ;; (default, stop matching)
    Fallthrough,      // ;& (execute next pattern's body)
    ContinueTesting,  // ;;& (test remaining patterns)
}

/// A function/method parameter.
#[derive(Debug, Clone, PartialEq)]
pub struct ParamDef {
    pub name: String,
    pub default_value: Option<Node>,
    pub is_named: bool,
}

/// An attribute definition in a class.
#[derive(Debug, Clone, PartialEq)]
pub struct AttrDef {
    pub name: String,
    pub type_name: Option<String>,
    pub default_value: Option<Node>,
}

/// A block literal: `{ |params| body }` or `do |params| ... end`
#[derive(Debug, Clone, PartialEq)]
pub struct BlockLiteral {
    pub params: Vec<String>,
    pub body: Vec<Node>,
}

/// Part of an interpolated string.
#[derive(Debug, Clone, PartialEq)]
pub enum StringPart {
    Text(String),
    Expr(Node),
}
