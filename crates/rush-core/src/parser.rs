use crate::ast::{AttrDef, BlockLiteral, Node, ParamDef, StringPart};
use crate::lexer::Lexer;
use crate::token::{Token, TokenType};

/// Known builtin function names that can be called without parentheses.
const BUILTIN_FUNCTIONS: &[&str] = &["puts", "print", "warn", "die", "ask", "sleep", "exit", "ai"];

fn is_builtin(name: &str) -> bool {
    BUILTIN_FUNCTIONS
        .iter()
        .any(|b| b.eq_ignore_ascii_case(name))
}

/// Parser error with position information.
#[derive(Debug, Clone)]
pub struct ParseError {
    pub message: String,
    pub position: usize,
}

impl std::fmt::Display for ParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Parse error at {}: {}", self.position, self.message)
    }
}

impl std::error::Error for ParseError {}

type ParseResult<T> = Result<T, ParseError>;

/// Recursive descent parser for Rush.
pub struct Parser {
    tokens: Vec<Token>,
    source: Option<String>,
    pos: usize,
}

impl Parser {
    pub fn new(tokens: Vec<Token>, source: Option<String>) -> Self {
        Self {
            tokens,
            source,
            pos: 0,
        }
    }

    fn current(&self) -> &Token {
        self.tokens.get(self.pos).unwrap_or(self.tokens.last().unwrap())
    }

    fn peek(&self, offset: usize) -> &Token {
        self.tokens
            .get(self.pos + offset)
            .unwrap_or(self.tokens.last().unwrap())
    }

    fn advance_clone(&mut self) -> Token {
        let tok = self.tokens[self.pos.min(self.tokens.len() - 1)].clone();
        self.pos += 1;
        tok
    }

    fn check(&self, tt: TokenType) -> bool {
        self.current().token_type == tt
    }

    fn expect(&mut self, tt: TokenType) -> ParseResult<Token> {
        if self.current().token_type != tt {
            return Err(ParseError {
                message: format!(
                    "Expected {:?}, got {:?} ('{}') at position {}",
                    tt,
                    self.current().token_type,
                    self.current().value,
                    self.current().position,
                ),
                position: self.current().position,
            });
        }
        Ok(self.advance_clone())
    }

    fn match_token(&mut self, tt: TokenType) -> bool {
        if self.current().token_type == tt {
            self.pos += 1;
            true
        } else {
            false
        }
    }

    fn skip_newlines(&mut self) {
        while matches!(
            self.current().token_type,
            TokenType::Newline | TokenType::Semicolon
        ) {
            self.pos += 1;
        }
    }

    fn err(&self, msg: impl Into<String>) -> ParseError {
        ParseError {
            message: msg.into(),
            position: self.current().position,
        }
    }

    // ── Public API ──────────────────────────────────────────────────

    /// Parse the entire token stream into a list of statements.
    pub fn parse(&mut self) -> ParseResult<Vec<Node>> {
        let mut statements = Vec::new();
        self.skip_newlines();

        while self.current().token_type != TokenType::Eof {
            if let Some(stmt) = self.parse_statement()? {
                statements.push(stmt);
            }
            self.skip_newlines();
        }

        Ok(statements)
    }

    // ── Statement Parsing ───────────────────────────────────────────

    fn parse_statement(&mut self) -> ParseResult<Option<Node>> {
        self.skip_newlines();
        if self.current().token_type == TokenType::Eof {
            return Ok(None);
        }

        let node = match self.current().token_type {
            TokenType::If => self.parse_if()?,
            TokenType::Unless => self.parse_unless()?,
            TokenType::For => self.parse_for()?,
            TokenType::While => self.parse_while()?,
            TokenType::Until => self.parse_until()?,
            TokenType::Loop => self.parse_loop()?,
            TokenType::Def => self.parse_function_def()?,
            TokenType::Class => self.parse_class_def()?,
            TokenType::Enum => self.parse_enum_def()?,
            TokenType::Return => self.parse_return()?,
            TokenType::Try | TokenType::Begin => self.parse_try()?,
            TokenType::Case => self.parse_case()?,
            TokenType::Macos | TokenType::Win64 | TokenType::Linux | TokenType::Isssh => {
                self.parse_platform_block()?
            }
            TokenType::Win32 => self.parse_win32_block()?,
            TokenType::Ps | TokenType::Ps5 => self.parse_raw_ps_block()?,
            TokenType::Plugin => self.parse_plugin_block()?,
            TokenType::Next | TokenType::Continue | TokenType::Break => {
                self.parse_loop_control()?
            }
            _ => self.parse_expression_statement()?,
        };

        Ok(Some(node))
    }

    // ── Control Flow ────────────────────────────────────────────────

    fn parse_if(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'if'
        let condition = self.parse_expression()?;
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::Elsif, TokenType::Else, TokenType::End])?;

        let mut elsifs = Vec::new();
        while self.check(TokenType::Elsif) {
            self.pos += 1;
            let cond = self.parse_expression()?;
            self.skip_newlines();
            let b = self.parse_body(&[TokenType::Elsif, TokenType::Else, TokenType::End])?;
            elsifs.push((cond, b));
        }

        let else_body = if self.check(TokenType::Else) {
            self.pos += 1;
            self.skip_newlines();
            Some(self.parse_body(&[TokenType::End])?)
        } else {
            None
        };

        self.expect(TokenType::End)?;
        Ok(Node::If {
            condition: Box::new(condition),
            body,
            elsifs,
            else_body,
        })
    }

    fn parse_unless(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'unless'
        let condition = self.parse_expression()?;
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::Else, TokenType::End])?;

        let negated = Node::UnaryOp {
            op: "not".to_string(),
            operand: Box::new(condition),
        };

        let else_body = if self.check(TokenType::Else) {
            self.pos += 1;
            self.skip_newlines();
            Some(self.parse_body(&[TokenType::End])?)
        } else {
            None
        };

        self.expect(TokenType::End)?;
        Ok(Node::If {
            condition: Box::new(negated),
            body,
            elsifs: Vec::new(),
            else_body,
        })
    }

    fn parse_for(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'for'
        let variable = self.expect(TokenType::Identifier)?.value;

        // "for name" without "in" → iterate over ARGV (positional params)
        let collection = if self.check(TokenType::In) {
            self.pos += 1; // skip 'in'
            self.parse_expression()?
        } else {
            // Default: iterate over ARGV
            Node::VariableRef { name: "ARGV".to_string() }
        };

        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;
        Ok(Node::For {
            variable,
            collection: Box::new(collection),
            body,
        })
    }

    fn parse_while(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'while'
        let condition = self.parse_expression()?;
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;
        Ok(Node::While {
            condition: Box::new(condition),
            body,
            is_until: false,
        })
    }

    fn parse_until(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'until'
        let condition = self.parse_expression()?;
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;
        Ok(Node::While {
            condition: Box::new(condition),
            body,
            is_until: true,
        })
    }

    fn parse_loop(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'loop'
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;
        Ok(Node::While {
            condition: Box::new(Node::Literal {
                value: "true".to_string(),
                literal_type: TokenType::True,
            }),
            body,
            is_until: false,
        })
    }

    // ── Definitions ─────────────────────────────────────────────────

    fn parse_function_def(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'def'
        let name = self.expect(TokenType::Identifier)?.value;
        let params = if self.match_token(TokenType::LParen) {
            let p = self.parse_param_list()?;
            self.expect(TokenType::RParen)?;
            p
        } else {
            Vec::new()
        };
        self.skip_newlines();

        // Capture raw body source for mixed Rush+shell function bodies
        let body_start = self.current().position;
        let body = self.parse_body(&[TokenType::End])?;
        let body_end = self.current().position;
        let raw_body = self.source.as_ref().map(|s| {
            s[body_start..body_end].trim().to_string()
        });

        self.expect(TokenType::End)?;
        Ok(Node::FunctionDef {
            name,
            params,
            body,
            is_static: false,
            raw_body,
        })
    }

    fn parse_param_list(&mut self) -> ParseResult<Vec<ParamDef>> {
        let mut params = Vec::new();
        if self.check(TokenType::RParen) {
            return Ok(params);
        }
        loop {
            let name = self.expect(TokenType::Identifier)?.value;
            let mut default_value = None;
            let mut is_named = false;

            if self.match_token(TokenType::Colon) {
                is_named = true;
                default_value = Some(self.parse_expression()?);
            } else if self.match_token(TokenType::Assign) {
                default_value = Some(self.parse_expression()?);
            }

            params.push(ParamDef {
                name,
                default_value,
                is_named,
            });

            if !self.match_token(TokenType::Comma) {
                break;
            }
        }
        Ok(params)
    }

    fn parse_class_def(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'class'
        let name = self.expect(TokenType::Identifier)?.value;

        let parent = if self.match_token(TokenType::LessThan) {
            Some(self.expect(TokenType::Identifier)?.value)
        } else {
            None
        };

        self.skip_newlines();

        let mut attributes = Vec::new();
        let mut constructor: Option<Box<Node>> = None;
        let mut methods = Vec::new();
        let mut static_methods = Vec::new();

        while !self.check(TokenType::End) && !self.check(TokenType::Eof) {
            self.skip_newlines();
            if self.check(TokenType::End) || self.check(TokenType::Eof) {
                break;
            }

            if self.check(TokenType::Attr) {
                self.pos += 1; // skip 'attr'
                loop {
                    let attr_name = self.expect(TokenType::Identifier)?.value;
                    let type_name = if self.match_token(TokenType::Colon) {
                        Some(self.expect(TokenType::Identifier)?.value)
                    } else {
                        None
                    };
                    let default_value = if self.match_token(TokenType::Assign) {
                        Some(self.parse_expression()?)
                    } else {
                        None
                    };
                    attributes.push(AttrDef {
                        name: attr_name,
                        type_name,
                        default_value,
                    });
                    if !self.match_token(TokenType::Comma) {
                        break;
                    }
                }
            } else if self.check(TokenType::Def) {
                self.pos += 1; // skip 'def'

                let mut is_static = false;
                if self.check(TokenType::SelfKw) && self.peek(1).token_type == TokenType::Dot {
                    is_static = true;
                    self.pos += 2; // skip 'self' and '.'
                }

                let method_name = self.expect(TokenType::Identifier)?.value;
                let params = if self.match_token(TokenType::LParen) {
                    let p = self.parse_param_list()?;
                    self.expect(TokenType::RParen)?;
                    p
                } else {
                    Vec::new()
                };

                self.skip_newlines();
                let body = self.parse_body(&[TokenType::End])?;
                self.expect(TokenType::End)?;

                let method = Node::FunctionDef {
                    name: method_name.clone(),
                    params,
                    body,
                    is_static,
                    raw_body: None, // class methods don't need mixed dispatch
                };

                if is_static {
                    static_methods.push(method);
                } else if method_name.eq_ignore_ascii_case("initialize") {
                    constructor = Some(Box::new(method));
                } else {
                    methods.push(method);
                }
            } else {
                return Err(self.err(format!(
                    "Unexpected {:?} in class body. Expected 'attr', 'def', or 'end'.",
                    self.current().token_type,
                )));
            }
            self.skip_newlines();
        }

        self.expect(TokenType::End)?;
        Ok(Node::ClassDef {
            name,
            parent,
            attributes,
            constructor,
            methods,
            static_methods,
        })
    }

    fn parse_enum_def(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'enum'
        let name = self.expect(TokenType::Identifier)?.value;
        self.skip_newlines();

        let mut members = Vec::new();
        while !self.check(TokenType::End) && !self.check(TokenType::Eof) {
            self.skip_newlines();
            if self.check(TokenType::End) || self.check(TokenType::Eof) {
                break;
            }
            let member_name = self.expect(TokenType::Identifier)?.value;
            let value = if self.match_token(TokenType::Assign) {
                Some(Box::new(self.parse_expression()?))
            } else {
                None
            };
            members.push((member_name, value));
            self.skip_newlines();
        }

        self.expect(TokenType::End)?;
        Ok(Node::EnumDef { name, members })
    }

    fn parse_return(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'return'
        let value = if !matches!(
            self.current().token_type,
            TokenType::Newline | TokenType::Eof | TokenType::End | TokenType::Semicolon
        ) {
            Some(Box::new(self.parse_expression()?))
        } else {
            None
        };
        Ok(Node::Return { value })
    }

    fn parse_try(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'try' or 'begin'
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::Rescue, TokenType::Ensure, TokenType::End])?;

        let mut rescue_var = None;
        let mut rescue_body = None;

        if self.check(TokenType::Rescue) {
            self.pos += 1;

            // rescue => e
            if self.check(TokenType::Assign) && self.peek(1).token_type == TokenType::GreaterThan {
                self.pos += 2; // skip =>
                if self.check(TokenType::Identifier) {
                    rescue_var = Some(self.advance_clone().value);
                }
            } else if self.check(TokenType::Identifier) {
                let first = self.advance_clone().value;
                if self.check(TokenType::Assign)
                    && self.peek(1).token_type == TokenType::GreaterThan
                {
                    self.pos += 2; // skip =>
                    if self.check(TokenType::Identifier) {
                        rescue_var = Some(self.advance_clone().value);
                    }
                } else {
                    rescue_var = Some(first);
                }
            }

            self.skip_newlines();
            rescue_body = Some(self.parse_body(&[TokenType::Ensure, TokenType::End])?);
        }

        let ensure_body = if self.check(TokenType::Ensure) {
            self.pos += 1;
            self.skip_newlines();
            Some(self.parse_body(&[TokenType::End])?)
        } else {
            None
        };

        self.expect(TokenType::End)?;
        Ok(Node::Try {
            body,
            rescue_var,
            rescue_body,
            ensure_body,
        })
    }

    fn parse_case(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'case'/'match'
        let subject = self.parse_expression()?;
        self.skip_newlines();

        let mut whens = Vec::new();
        while self.check(TokenType::When) {
            self.pos += 1;
            let pattern = self.parse_expression()?;
            self.skip_newlines();
            let when_body =
                self.parse_body(&[TokenType::When, TokenType::Else, TokenType::End])?;
            whens.push((pattern, when_body, crate::ast::CaseTerminator::Break));
        }

        let else_body = if self.check(TokenType::Else) {
            self.pos += 1;
            self.skip_newlines();
            Some(self.parse_body(&[TokenType::End])?)
        } else {
            None
        };

        self.expect(TokenType::End)?;
        Ok(Node::Case {
            subject: Box::new(subject),
            whens,
            else_body,
        })
    }

    // ── Platform Blocks ─────────────────────────────────────────────

    fn parse_platform_block(&mut self) -> ParseResult<Node> {
        let platform = self.advance_clone().value.to_ascii_lowercase();

        let (property, operator, property_value) = self.parse_platform_condition()?;

        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;

        Ok(Node::PlatformBlock {
            platform,
            body: Some(body),
            raw_body: None,
            property,
            operator,
            property_value,
        })
    }

    fn parse_win32_block(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'win32'
        self.skip_newlines();
        let raw_body = self.capture_raw_body();
        self.expect(TokenType::End)?;
        Ok(Node::PlatformBlock {
            platform: "win32".to_string(),
            body: None,
            raw_body: Some(raw_body),
            property: None,
            operator: None,
            property_value: None,
        })
    }

    fn parse_raw_ps_block(&mut self) -> ParseResult<Node> {
        let platform = self.advance_clone().value.to_ascii_lowercase();
        let (property, operator, property_value) = self.parse_platform_condition()?;
        self.skip_newlines();
        let raw_body = self.capture_raw_body();
        self.expect(TokenType::End)?;

        Ok(Node::PlatformBlock {
            platform,
            body: None,
            raw_body: Some(raw_body),
            property,
            operator,
            property_value,
        })
    }

    /// Parse `plugin.NAME ... end` — raw body sent to companion binary.
    fn parse_plugin_block(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'plugin'

        // Expect dot + name
        if !self.check(TokenType::Dot) {
            return Err(ParseError {
                message: "Expected '.' after 'plugin' (e.g., plugin.ps)".into(),
                position: self.current().position,
            });
        }
        self.pos += 1; // skip '.'

        // Accept identifiers OR keywords as plugin names (ps, win32, python, etc.)
        let name = self.advance_clone().value.to_ascii_lowercase();

        self.skip_newlines();
        let raw_body = self.capture_raw_body();
        self.expect(TokenType::End)?;

        Ok(Node::PluginBlock {
            plugin_name: name,
            raw_body,
        })
    }

    fn parse_platform_condition(
        &mut self,
    ) -> ParseResult<(Option<String>, Option<String>, Option<String>)> {
        if !self.check(TokenType::Dot) {
            return Ok((None, None, None));
        }
        self.pos += 1; // skip '.'
        let property = self.expect(TokenType::Identifier)?.value.to_ascii_lowercase();

        let op = match self.current().token_type {
            TokenType::Equals
            | TokenType::NotEquals
            | TokenType::GreaterThan
            | TokenType::GreaterEqual
            | TokenType::LessThan
            | TokenType::LessEqual => self.advance_clone().value,
            _ => {
                return Err(
                    self.err(format!("Expected comparison operator after .{property}"))
                );
            }
        };

        let value = if self.check(TokenType::StringLiteral) {
            let raw = self.advance_clone().value;
            raw.trim_matches('"').trim_matches('\'').to_string()
        } else if self.check(TokenType::Identifier) {
            self.advance_clone().value
        } else {
            return Err(self.err(format!("Expected value after .{property} {op}")));
        };

        Ok((Some(property), Some(op), Some(value)))
    }

    /// Capture raw text body (for win32/ps/ps5 blocks) by collecting token values.
    fn capture_raw_body(&mut self) -> String {
        if let Some(ref source) = self.source {
            if self.current().token_type != TokenType::End
                && self.current().token_type != TokenType::Eof
            {
                let start_pos = self.current().position;
                while self.current().token_type != TokenType::End
                    && self.current().token_type != TokenType::Eof
                {
                    self.pos += 1;
                }
                let end_pos = self.current().position;
                return source[start_pos..end_pos].trim().to_string();
            }
        }

        // Fallback: reconstruct from tokens
        let mut result = String::new();
        while self.current().token_type != TokenType::End
            && self.current().token_type != TokenType::Eof
        {
            if self.current().token_type == TokenType::Newline {
                result.push('\n');
            } else {
                if !result.is_empty() && !result.ends_with('\n') {
                    result.push(' ');
                }
                result.push_str(&self.current().value);
            }
            self.pos += 1;
        }
        result.trim().to_string()
    }

    // ── Loop Control ────────────────────────────────────────────────

    fn parse_loop_control(&mut self) -> ParseResult<Node> {
        let keyword = self.advance_clone().value;
        let node = Node::LoopControl { keyword };
        self.wrap_postfix(node)
    }

    // ── Expression Statements ───────────────────────────────────────

    fn parse_expression_statement(&mut self) -> ParseResult<Node> {
        // Compound assignment: identifier += expr
        if self.check(TokenType::Identifier)
            && matches!(
                self.peek(1).token_type,
                TokenType::PlusAssign
                    | TokenType::MinusAssign
                    | TokenType::StarAssign
                    | TokenType::SlashAssign
            )
        {
            let name = self.advance_clone().value;
            let op = self.advance_clone().value;
            let value = self.parse_expression()?;
            return self.wrap_postfix(Node::CompoundAssignment {
                name,
                op,
                value: Box::new(value),
            });
        }

        // Multiple assignment: a, b, c = 1, 2, 3
        if self.check(TokenType::Identifier) && self.peek(1).token_type == TokenType::Comma {
            let saved = self.pos;
            let mut names = vec![self.advance_clone().value];
            while self.match_token(TokenType::Comma) {
                if !self.check(TokenType::Identifier) {
                    names.clear();
                    break;
                }
                names.push(self.advance_clone().value);
            }
            if names.len() >= 2
                && self.check(TokenType::Assign)
                && self.peek(1).token_type != TokenType::Assign
            {
                self.pos += 1; // skip =
                let mut values = vec![self.parse_expression()?];
                while self.match_token(TokenType::Comma) {
                    values.push(self.parse_expression()?);
                }
                return self.wrap_postfix(Node::MultipleAssignment { names, values });
            }
            self.pos = saved;
        }

        // Simple assignment: identifier = expr
        if self.check(TokenType::Identifier)
            && self.peek(1).token_type == TokenType::Assign
            && self.peek(2).token_type != TokenType::Assign
        {
            let name = self.advance_clone().value;
            self.pos += 1; // skip =
            let value = self.parse_expression()?;
            return self.wrap_postfix(Node::Assignment {
                name,
                value: Box::new(value),
            });
        }

        let expr = self.parse_expression()?;

        // Property assignment: expr.property = value
        if let Node::PropertyAccess {
            ref receiver,
            ref property,
        } = expr
        {
            if self.check(TokenType::Assign) && self.peek(1).token_type != TokenType::Assign {
                self.pos += 1; // skip =
                let value = self.parse_expression()?;
                return self.wrap_postfix(Node::PropertyAssignment {
                    receiver: receiver.clone(),
                    property: property.clone(),
                    value: Box::new(value),
                });
            }
        }

        self.wrap_postfix(expr)
    }

    /// Wrap in PostfixIf if followed by `if` or `unless`.
    fn wrap_postfix(&mut self, statement: Node) -> ParseResult<Node> {
        if self.check(TokenType::If) {
            self.pos += 1;
            let condition = self.parse_expression()?;
            return Ok(Node::PostfixIf {
                statement: Box::new(statement),
                condition: Box::new(condition),
                is_unless: false,
            });
        }
        if self.check(TokenType::Unless) {
            self.pos += 1;
            let condition = self.parse_expression()?;
            return Ok(Node::PostfixIf {
                statement: Box::new(statement),
                condition: Box::new(condition),
                is_unless: true,
            });
        }
        Ok(statement)
    }

    // ── Expression Parsing (Precedence Climbing) ────────────────────

    /// Public entry point for expression parsing.
    pub fn parse_expression(&mut self) -> ParseResult<Node> {
        let expr = self.parse_pipe_pipe()?;
        if self.check(TokenType::QuestionMark) {
            self.pos += 1;
            let then_expr = self.parse_expression()?;
            self.expect(TokenType::Colon)?;
            let else_expr = self.parse_expression()?;
            return Ok(Node::Ternary {
                condition: Box::new(expr),
                then_expr: Box::new(then_expr),
                else_expr: Box::new(else_expr),
            });
        }
        Ok(expr)
    }

    fn parse_pipe_pipe(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_amp_amp()?;
        while self.check(TokenType::PipePipe) {
            self.pos += 1;
            let right = self.parse_amp_amp()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op: "||".to_string(),
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_amp_amp(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_or()?;
        while self.check(TokenType::AmpAmp) {
            self.pos += 1;
            let right = self.parse_or()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op: "&&".to_string(),
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_or(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_and()?;
        while self.check(TokenType::Or) {
            self.pos += 1;
            let right = self.parse_and()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op: "or".to_string(),
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_and(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_not()?;
        while self.check(TokenType::And) {
            self.pos += 1;
            let right = self.parse_not()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op: "and".to_string(),
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_not(&mut self) -> ParseResult<Node> {
        if self.check(TokenType::Not) {
            self.pos += 1;
            let operand = self.parse_not()?;
            return Ok(Node::UnaryOp {
                op: "not".to_string(),
                operand: Box::new(operand),
            });
        }
        self.parse_comparison()
    }

    fn parse_comparison(&mut self) -> ParseResult<Node> {
        let left = self.parse_range()?;
        if matches!(
            self.current().token_type,
            TokenType::Equals
                | TokenType::NotEquals
                | TokenType::LessThan
                | TokenType::GreaterThan
                | TokenType::LessEqual
                | TokenType::GreaterEqual
                | TokenType::Match
                | TokenType::MatchOp
                | TokenType::NotMatch
        ) {
            let op_tok = self.advance_clone();
            let op_str = if op_tok.token_type == TokenType::MatchOp {
                "=~".to_string()
            } else {
                op_tok.value
            };
            let right = self.parse_range()?;
            return Ok(Node::BinaryOp {
                left: Box::new(left),
                op: op_str,
                right: Box::new(right),
            });
        }
        Ok(left)
    }

    fn parse_range(&mut self) -> ParseResult<Node> {
        let left = self.parse_additive()?;
        if self.check(TokenType::DotDot) {
            self.pos += 1;
            let right = self.parse_additive()?;
            return Ok(Node::Range {
                start: Box::new(left),
                end: Box::new(right),
                exclusive: false,
            });
        }
        if self.check(TokenType::DotDotDot) {
            self.pos += 1;
            let right = self.parse_additive()?;
            return Ok(Node::Range {
                start: Box::new(left),
                end: Box::new(right),
                exclusive: true,
            });
        }
        Ok(left)
    }

    fn parse_additive(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_multiplicative()?;
        while matches!(
            self.current().token_type,
            TokenType::Plus | TokenType::Minus
        ) {
            let op = self.advance_clone().value;
            let right = self.parse_multiplicative()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op,
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_multiplicative(&mut self) -> ParseResult<Node> {
        let mut left = self.parse_unary_minus()?;
        while matches!(
            self.current().token_type,
            TokenType::Star | TokenType::Slash | TokenType::Percent
        ) {
            let op = self.advance_clone().value;
            let right = self.parse_unary_minus()?;
            left = Node::BinaryOp {
                left: Box::new(left),
                op,
                right: Box::new(right),
            };
        }
        Ok(left)
    }

    fn parse_unary_minus(&mut self) -> ParseResult<Node> {
        if self.check(TokenType::Minus) {
            self.pos += 1;
            let operand = self.parse_postfix()?;
            return Ok(Node::UnaryOp {
                op: "-".to_string(),
                operand: Box::new(operand),
            });
        }
        self.parse_postfix()
    }

    // ── Postfix Operations ──────────────────────────────────────────

    fn parse_postfix(&mut self) -> ParseResult<Node> {
        let mut node = self.parse_primary()?;

        loop {
            if self.check(TokenType::Dot) {
                self.pos += 1;
                let member = self.expect(TokenType::Identifier)?.value;

                if self.check(TokenType::LBrace) && self.is_block_start() {
                    let block = self.parse_block()?;
                    node = Node::MethodCall {
                        receiver: Box::new(node),
                        method: member,
                        args: Vec::new(),
                        block: Some(Box::new(block)),
                    };
                } else if self.check(TokenType::Do) {
                    let block = self.parse_do_block()?;
                    node = Node::MethodCall {
                        receiver: Box::new(node),
                        method: member,
                        args: Vec::new(),
                        block: Some(Box::new(block)),
                    };
                } else if self.check(TokenType::LParen) {
                    let args = self.parse_arg_list()?;
                    let block = if self.check(TokenType::LBrace) && self.is_block_start() {
                        Some(Box::new(self.parse_block()?))
                    } else if self.check(TokenType::Do) {
                        Some(Box::new(self.parse_do_block()?))
                    } else {
                        None
                    };
                    node = Node::MethodCall {
                        receiver: Box::new(node),
                        method: member,
                        args,
                        block,
                    };
                } else {
                    node = Node::PropertyAccess {
                        receiver: Box::new(node),
                        property: member,
                    };
                }
            } else if self.check(TokenType::SafeNav) {
                self.pos += 1;
                let member = self.expect(TokenType::Identifier)?.value;
                if self.check(TokenType::LParen) {
                    let _args = self.parse_arg_list()?;
                }
                node = Node::SafeNav {
                    receiver: Box::new(node),
                    member,
                };
            } else if self.check(TokenType::LBracket) {
                self.pos += 1;
                let index = self.parse_expression()?;
                self.expect(TokenType::RBracket)?;
                node = Node::MethodCall {
                    receiver: Box::new(node),
                    method: "[]".to_string(),
                    args: vec![index],
                    block: None,
                };
            } else {
                break;
            }
        }

        Ok(node)
    }

    fn is_block_start(&self) -> bool {
        if self.current().token_type != TokenType::LBrace {
            return false;
        }
        let mut i = self.pos + 1;
        while i < self.tokens.len() && self.tokens[i].token_type == TokenType::Newline {
            i += 1;
        }
        i < self.tokens.len() && self.tokens[i].token_type == TokenType::Pipe
    }

    fn parse_block(&mut self) -> ParseResult<BlockLiteral> {
        self.expect(TokenType::LBrace)?;
        let mut params = Vec::new();
        if self.match_token(TokenType::Pipe) {
            loop {
                params.push(self.expect(TokenType::Identifier)?.value);
                if !self.match_token(TokenType::Comma) {
                    break;
                }
            }
            self.expect(TokenType::Pipe)?;
        }
        self.skip_newlines();
        let mut body = Vec::new();
        while !self.check(TokenType::RBrace) && !self.check(TokenType::Eof) {
            if let Some(stmt) = self.parse_statement()? {
                body.push(stmt);
            }
            self.skip_newlines();
        }
        self.expect(TokenType::RBrace)?;
        Ok(BlockLiteral { params, body })
    }

    fn parse_do_block(&mut self) -> ParseResult<BlockLiteral> {
        self.expect(TokenType::Do)?;
        let mut params = Vec::new();
        if self.match_token(TokenType::Pipe) {
            loop {
                params.push(self.expect(TokenType::Identifier)?.value);
                if !self.match_token(TokenType::Comma) {
                    break;
                }
            }
            self.expect(TokenType::Pipe)?;
        }
        self.skip_newlines();
        let body = self.parse_body(&[TokenType::End])?;
        self.expect(TokenType::End)?;
        Ok(BlockLiteral { params, body })
    }

    fn parse_arg_list(&mut self) -> ParseResult<Vec<Node>> {
        self.expect(TokenType::LParen)?;
        let mut args = Vec::new();
        if !self.check(TokenType::RParen) {
            loop {
                self.skip_newlines();
                // Named arg: identifier: value
                if self.check(TokenType::Identifier)
                    && self.peek(1).token_type == TokenType::Colon
                {
                    let name = self.advance_clone().value;
                    self.pos += 1; // skip :
                    let value = self.parse_expression()?;
                    args.push(Node::NamedArg {
                        name,
                        value: Box::new(value),
                    });
                } else {
                    args.push(self.parse_expression()?);
                }
                self.skip_newlines();
                if !self.match_token(TokenType::Comma) {
                    break;
                }
            }
        }
        self.expect(TokenType::RParen)?;
        Ok(args)
    }

    // ── Primary Expressions ─────────────────────────────────────────

    fn parse_primary(&mut self) -> ParseResult<Node> {
        match self.current().token_type {
            TokenType::Integer | TokenType::Float => {
                let tok = self.advance_clone();
                Ok(Node::Literal {
                    value: tok.value,
                    literal_type: tok.token_type,
                })
            }

            TokenType::StringLiteral => self.parse_string_literal(),

            TokenType::True => {
                self.pos += 1;
                Ok(Node::Literal {
                    value: "true".to_string(),
                    literal_type: TokenType::True,
                })
            }
            TokenType::False => {
                self.pos += 1;
                Ok(Node::Literal {
                    value: "false".to_string(),
                    literal_type: TokenType::False,
                })
            }
            TokenType::Nil => {
                self.pos += 1;
                Ok(Node::Literal {
                    value: "nil".to_string(),
                    literal_type: TokenType::Nil,
                })
            }

            TokenType::Symbol => {
                let tok = self.advance_clone();
                Ok(Node::Symbol { name: tok.value })
            }

            TokenType::SelfKw => {
                self.pos += 1;
                Ok(Node::VariableRef {
                    name: "self".to_string(),
                })
            }

            TokenType::Super => self.parse_super_expr(),

            TokenType::Identifier => self.parse_identifier_expr(),

            TokenType::LParen => {
                self.pos += 1;
                let expr = self.parse_expression()?;
                self.expect(TokenType::RParen)?;
                Ok(expr)
            }

            TokenType::LBracket => {
                // Check for [Type]::Member
                let mut lookahead = 1;
                while matches!(
                    self.peek(lookahead).token_type,
                    TokenType::Identifier | TokenType::Dot
                ) {
                    lookahead += 1;
                }
                if self.peek(lookahead).token_type == TokenType::RBracket
                    && self.peek(lookahead + 1).token_type == TokenType::DoubleColon
                {
                    self.pos += 1; // skip [
                    let mut type_parts = vec![self.expect(TokenType::Identifier)?.value];
                    while self.check(TokenType::Dot) {
                        self.pos += 1;
                        type_parts.push(self.expect(TokenType::Identifier)?.value);
                    }
                    let type_name = type_parts.join(".");
                    self.expect(TokenType::RBracket)?;
                    self.pos += 1; // skip ::
                    let member = self.expect(TokenType::Identifier)?.value;

                    let args = if self.check(TokenType::LParen) {
                        self.pos += 1;
                        let mut a = Vec::new();
                        if !self.check(TokenType::RParen) {
                            a.push(self.parse_expression()?);
                            while self.match_token(TokenType::Comma) {
                                a.push(self.parse_expression()?);
                            }
                        }
                        self.expect(TokenType::RParen)?;
                        Some(a)
                    } else {
                        None
                    };

                    return Ok(Node::StaticMember {
                        type_name,
                        member,
                        args,
                    });
                }
                self.parse_array_literal()
            }

            TokenType::LBrace => self.parse_hash_literal(),

            TokenType::DollarParen => {
                let tok = self.advance_clone();
                Ok(Node::CommandSub {
                    command: tok.value,
                })
            }

            TokenType::DollarQuestion => {
                self.pos += 1;
                Ok(Node::VariableRef {
                    name: "$?".to_string(),
                })
            }

            TokenType::Regex => {
                let tok = self.advance_clone();
                let parts: Vec<&str> = tok.value.splitn(2, '\0').collect();
                Ok(Node::RegexLiteral {
                    pattern: parts[0].to_string(),
                    flags: parts.get(1).unwrap_or(&"").to_string(),
                })
            }

            _ => Err(self.err(format!(
                "Unexpected token {:?} ('{}')",
                self.current().token_type,
                self.current().value,
            ))),
        }
    }

    fn parse_super_expr(&mut self) -> ParseResult<Node> {
        self.pos += 1; // skip 'super'

        if self.match_token(TokenType::Dot) {
            let method_name = self.expect(TokenType::Identifier)?.value;
            let args = if self.match_token(TokenType::LParen) {
                let mut a = Vec::new();
                if !self.check(TokenType::RParen) {
                    loop {
                        a.push(self.parse_expression()?);
                        if !self.match_token(TokenType::Comma) {
                            break;
                        }
                    }
                }
                self.expect(TokenType::RParen)?;
                a
            } else {
                Vec::new()
            };
            return Ok(Node::SuperCall {
                args,
                method_name: Some(method_name),
            });
        }

        if self.match_token(TokenType::LParen) {
            let mut args = Vec::new();
            if !self.check(TokenType::RParen) {
                loop {
                    args.push(self.parse_expression()?);
                    if !self.match_token(TokenType::Comma) {
                        break;
                    }
                }
            }
            self.expect(TokenType::RParen)?;
            return Ok(Node::SuperCall {
                args,
                method_name: None,
            });
        }

        Ok(Node::SuperCall {
            args: Vec::new(),
            method_name: None,
        })
    }

    fn parse_identifier_expr(&mut self) -> ParseResult<Node> {
        let name_tok = self.advance_clone();
        let name = &name_tok.value;

        // Function call with parens
        if self.check(TokenType::LParen) {
            let is_adjacent = self.current().position == name_tok.position + name_tok.value.len();
            if is_adjacent || !is_builtin(name) {
                let args = self.parse_arg_list()?;
                let block = if self.check(TokenType::LBrace) && self.is_block_start() {
                    Some(Box::new(self.parse_block()?))
                } else if self.check(TokenType::Do) {
                    Some(Box::new(self.parse_do_block()?))
                } else {
                    None
                };
                if let Some(block) = block {
                    return Ok(Node::MethodCall {
                        receiver: Box::new(Node::FunctionCall {
                            name: name.clone(),
                            args,
                        }),
                        method: "block".to_string(),
                        args: Vec::new(),
                        block: Some(block),
                    });
                }
                return Ok(Node::FunctionCall {
                    name: name.clone(),
                    args,
                });
            }
        }

        // Builtin call without parens
        if is_builtin(name) && self.is_expression_start() {
            let arg = self.parse_expression()?;
            return Ok(Node::FunctionCall {
                name: name.clone(),
                args: vec![arg],
            });
        }

        Ok(Node::VariableRef {
            name: name.clone(),
        })
    }

    fn is_expression_start(&self) -> bool {
        matches!(
            self.current().token_type,
            TokenType::StringLiteral
                | TokenType::Integer
                | TokenType::Float
                | TokenType::Identifier
                | TokenType::LParen
                | TokenType::LBracket
                | TokenType::Not
                | TokenType::True
                | TokenType::False
                | TokenType::Nil
                | TokenType::Minus
                | TokenType::Symbol
                | TokenType::DollarParen
                | TokenType::DollarQuestion
        )
    }

    fn parse_string_literal(&mut self) -> ParseResult<Node> {
        let tok = self.advance_clone();
        let raw = &tok.value;

        // Single-quoted: no interpolation
        if raw.starts_with('\'') {
            return Ok(Node::Literal {
                value: tok.value,
                literal_type: TokenType::StringLiteral,
            });
        }

        // Double-quoted without interpolation
        if !raw.contains("#{") {
            return Ok(Node::Literal {
                value: tok.value,
                literal_type: TokenType::StringLiteral,
            });
        }

        // Parse interpolated string
        let content = &raw[1..raw.len() - 1]; // strip quotes
        let mut parts = Vec::new();
        let chars: Vec<char> = content.chars().collect();
        let mut i = 0;
        let mut text_buf = String::new();

        while i < chars.len() {
            if i + 1 < chars.len() && chars[i] == '#' && chars[i + 1] == '{' {
                if !text_buf.is_empty() {
                    parts.push(StringPart::Text(text_buf.clone()));
                    text_buf.clear();
                }
                i += 2; // skip #{
                let mut depth = 1;
                let mut expr_buf = String::new();
                while i < chars.len() && depth > 0 {
                    if chars[i] == '{' {
                        depth += 1;
                    } else if chars[i] == '}' {
                        depth -= 1;
                    }
                    if depth > 0 {
                        expr_buf.push(chars[i]);
                    }
                    i += 1;
                }
                let expr_tokens = Lexer::new(&expr_buf).tokenize();
                let mut expr_parser = Parser::new(expr_tokens, None);
                let expr_node = expr_parser.parse_expression()?;
                parts.push(StringPart::Expr(expr_node));
            } else if chars[i] == '\\' && i + 1 < chars.len() {
                text_buf.push(chars[i]);
                text_buf.push(chars[i + 1]);
                i += 2;
            } else {
                text_buf.push(chars[i]);
                i += 1;
            }
        }

        if !text_buf.is_empty() {
            parts.push(StringPart::Text(text_buf));
        }

        // If it turned out to be just text, return as plain literal
        if parts.len() == 1 && matches!(&parts[0], StringPart::Text(_)) {
            return Ok(Node::Literal {
                value: tok.value,
                literal_type: TokenType::StringLiteral,
            });
        }

        Ok(Node::InterpolatedString { parts })
    }

    fn parse_array_literal(&mut self) -> ParseResult<Node> {
        self.expect(TokenType::LBracket)?;
        let mut elements = Vec::new();
        if !self.check(TokenType::RBracket) {
            loop {
                self.skip_newlines();
                elements.push(self.parse_expression()?);
                self.skip_newlines();
                if !self.match_token(TokenType::Comma) {
                    break;
                }
            }
        }
        self.expect(TokenType::RBracket)?;
        Ok(Node::Array { elements })
    }

    fn parse_hash_literal(&mut self) -> ParseResult<Node> {
        self.expect(TokenType::LBrace)?;
        self.skip_newlines();

        let mut entries = Vec::new();
        if !self.check(TokenType::RBrace) {
            loop {
                self.skip_newlines();
                if self.check(TokenType::Identifier) && self.peek(1).token_type == TokenType::Colon
                {
                    let key_name = self.advance_clone().value;
                    self.pos += 1; // skip :
                    self.skip_newlines();
                    let value = self.parse_expression()?;
                    entries.push((
                        Node::Symbol {
                            name: format!(":{key_name}"),
                        },
                        value,
                    ));
                } else {
                    let key = self.parse_expression()?;
                    self.expect(TokenType::Colon)?;
                    self.skip_newlines();
                    let value = self.parse_expression()?;
                    entries.push((key, value));
                }
                self.skip_newlines();
                if !self.match_token(TokenType::Comma) {
                    break;
                }
            }
        }

        self.skip_newlines();
        self.expect(TokenType::RBrace)?;
        Ok(Node::Hash { entries })
    }

    // ── Helpers ─────────────────────────────────────────────────────

    fn parse_body(&mut self, stop_tokens: &[TokenType]) -> ParseResult<Vec<Node>> {
        let mut statements = Vec::new();
        self.skip_newlines();

        while self.current().token_type != TokenType::Eof
            && !stop_tokens.contains(&self.current().token_type)
        {
            if let Some(stmt) = self.parse_statement()? {
                statements.push(stmt);
            }
            self.skip_newlines();
        }

        Ok(statements)
    }
}

/// Convenience: parse a source string directly.
pub fn parse(source: &str) -> ParseResult<Vec<Node>> {
    let tokens = Lexer::new(source).tokenize();
    let mut parser = Parser::new(tokens, Some(source.to_string()));
    parser.parse()
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── Assignment ──────────────────────────────────────────────────

    #[test]
    fn simple_assignment() {
        let nodes = parse("x = 42").unwrap();
        assert_eq!(nodes.len(), 1);
        assert!(matches!(&nodes[0], Node::Assignment { name, .. } if name == "x"));
    }

    #[test]
    fn multiple_assignment() {
        let nodes = parse("a, b = 1, 2").unwrap();
        assert_eq!(nodes.len(), 1);
        assert!(
            matches!(&nodes[0], Node::MultipleAssignment { names, values } if names.len() == 2 && values.len() == 2)
        );
    }

    #[test]
    fn compound_assignment() {
        let nodes = parse("x += 1").unwrap();
        assert!(matches!(&nodes[0], Node::CompoundAssignment { op, .. } if op == "+="));
    }

    // ── If / Unless ─────────────────────────────────────────────────

    #[test]
    fn if_else_end() {
        let nodes = parse("if x > 5\n  puts \"big\"\nelse\n  puts \"small\"\nend").unwrap();
        assert_eq!(nodes.len(), 1);
        assert!(matches!(&nodes[0], Node::If { else_body: Some(_), .. }));
    }

    #[test]
    fn if_elsif_end() {
        let nodes = parse("if x == 1\n  a\nelsif x == 2\n  b\nend").unwrap();
        assert!(matches!(&nodes[0], Node::If { elsifs, .. } if elsifs.len() == 1));
    }

    #[test]
    fn postfix_if() {
        let nodes = parse("puts \"hello\" if x > 0").unwrap();
        assert!(matches!(&nodes[0], Node::PostfixIf { is_unless: false, .. }));
    }

    #[test]
    fn postfix_unless() {
        let nodes = parse("puts \"err\" unless ok").unwrap();
        assert!(matches!(&nodes[0], Node::PostfixIf { is_unless: true, .. }));
    }

    // ── Loops ───────────────────────────────────────────────────────

    #[test]
    fn for_loop() {
        let nodes = parse("for x in [1,2,3]\n  puts x\nend").unwrap();
        assert!(matches!(&nodes[0], Node::For { variable, .. } if variable == "x"));
    }

    #[test]
    fn while_loop() {
        let nodes = parse("while x > 0\n  x -= 1\nend").unwrap();
        assert!(matches!(&nodes[0], Node::While { is_until: false, .. }));
    }

    #[test]
    fn until_loop() {
        let nodes = parse("until done\n  work()\nend").unwrap();
        assert!(matches!(&nodes[0], Node::While { is_until: true, .. }));
    }

    #[test]
    fn loop_infinite() {
        let nodes = parse("loop\n  run()\nend").unwrap();
        assert!(matches!(&nodes[0], Node::While { .. }));
    }

    // ── Functions ───────────────────────────────────────────────────

    #[test]
    fn function_def() {
        let nodes = parse("def greet(name)\n  puts name\nend").unwrap();
        assert!(
            matches!(&nodes[0], Node::FunctionDef { name, params, .. } if name == "greet" && params.len() == 1)
        );
    }

    #[test]
    fn function_default_param() {
        let nodes = parse("def greet(name = \"world\")\n  puts name\nend").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::FunctionDef { params, .. } if params[0].default_value.is_some()
        ));
    }

    #[test]
    fn function_call() {
        let nodes = parse("greet(\"hello\")").unwrap();
        assert!(matches!(&nodes[0], Node::FunctionCall { name, .. } if name == "greet"));
    }

    // ── Classes ─────────────────────────────────────────────────────

    #[test]
    fn class_def() {
        let src = "class Dog\n  attr name, breed\n  def bark\n    puts \"woof\"\n  end\nend";
        let nodes = parse(src).unwrap();
        assert!(
            matches!(&nodes[0], Node::ClassDef { name, attributes, methods, .. } if name == "Dog" && attributes.len() == 2 && methods.len() == 1)
        );
    }

    #[test]
    fn class_inheritance() {
        let src = "class Puppy < Dog\n  def play\n    puts \"play\"\n  end\nend";
        let nodes = parse(src).unwrap();
        assert!(matches!(
            &nodes[0],
            Node::ClassDef { parent: Some(p), .. } if p == "Dog"
        ));
    }

    // ── Enums ───────────────────────────────────────────────────────

    #[test]
    fn enum_def() {
        let nodes = parse("enum Color\n  Red\n  Green\n  Blue\nend").unwrap();
        assert!(
            matches!(&nodes[0], Node::EnumDef { name, members } if name == "Color" && members.len() == 3)
        );
    }

    // ── Try/Rescue ──────────────────────────────────────────────────

    #[test]
    fn try_rescue() {
        let nodes = parse("try\n  risky()\nrescue => e\n  puts e\nend").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::Try { rescue_var: Some(v), rescue_body: Some(_), .. } if v == "e"
        ));
    }

    #[test]
    fn try_ensure() {
        let nodes = parse("try\n  open()\nensure\n  close()\nend").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::Try { ensure_body: Some(_), .. }
        ));
    }

    // ── Case/When ───────────────────────────────────────────────────

    #[test]
    fn case_when() {
        let src = "case x\nwhen 1\n  puts \"one\"\nwhen 2\n  puts \"two\"\nelse\n  puts \"other\"\nend";
        let nodes = parse(src).unwrap();
        assert!(
            matches!(&nodes[0], Node::Case { whens, else_body: Some(_), .. } if whens.len() == 2)
        );
    }

    // ── Expressions ─────────────────────────────────────────────────

    #[test]
    fn binary_arithmetic() {
        let nodes = parse("x = 1 + 2 * 3").unwrap();
        // Should parse as x = (1 + (2 * 3))
        if let Node::Assignment { value, .. } = &nodes[0] {
            assert!(matches!(value.as_ref(), Node::BinaryOp { op, .. } if op == "+"));
        } else {
            panic!("Expected assignment");
        }
    }

    #[test]
    fn comparison() {
        let nodes = parse("x > 5").unwrap();
        assert!(matches!(&nodes[0], Node::BinaryOp { op, .. } if op == ">"));
    }

    #[test]
    fn range_inclusive() {
        let nodes = parse("1..10").unwrap();
        assert!(matches!(&nodes[0], Node::Range { exclusive: false, .. }));
    }

    #[test]
    fn range_exclusive() {
        let nodes = parse("1...10").unwrap();
        assert!(matches!(&nodes[0], Node::Range { exclusive: true, .. }));
    }

    #[test]
    fn ternary() {
        let nodes = parse("x > 0 ? \"pos\" : \"neg\"").unwrap();
        assert!(matches!(&nodes[0], Node::Ternary { .. }));
    }

    #[test]
    fn unary_minus() {
        let nodes = parse("-42").unwrap();
        assert!(matches!(&nodes[0], Node::UnaryOp { op, .. } if op == "-"));
    }

    #[test]
    fn not_expr() {
        let nodes = parse("not true").unwrap();
        assert!(matches!(&nodes[0], Node::UnaryOp { op, .. } if op == "not"));
    }

    // ── Method Calls ────────────────────────────────────────────────

    #[test]
    fn method_call_with_args() {
        let nodes = parse("items.push(42)").unwrap();
        assert!(matches!(&nodes[0], Node::MethodCall { method, .. } if method == "push"));
    }

    #[test]
    fn property_access() {
        let nodes = parse("item.name").unwrap();
        assert!(
            matches!(&nodes[0], Node::PropertyAccess { property, .. } if property == "name")
        );
    }

    #[test]
    fn chained_methods() {
        let nodes = parse("items.sort.first").unwrap();
        assert!(matches!(&nodes[0], Node::PropertyAccess { .. }));
    }

    #[test]
    fn method_with_block() {
        let nodes = parse("items.each { |x| puts x }").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::MethodCall { block: Some(_), .. }
        ));
    }

    #[test]
    fn index_access() {
        let nodes = parse("arr[0]").unwrap();
        assert!(matches!(&nodes[0], Node::MethodCall { method, .. } if method == "[]"));
    }

    // ── Literals ────────────────────────────────────────────────────

    #[test]
    fn array_literal() {
        let nodes = parse("[1, 2, 3]").unwrap();
        assert!(matches!(&nodes[0], Node::Array { elements } if elements.len() == 3));
    }

    #[test]
    fn hash_literal() {
        let nodes = parse("{a: 1, b: 2}").unwrap();
        assert!(matches!(&nodes[0], Node::Hash { entries } if entries.len() == 2));
    }

    #[test]
    fn symbol_literal() {
        let nodes = parse(":name").unwrap();
        assert!(matches!(&nodes[0], Node::Symbol { name } if name == ":name"));
    }

    #[test]
    fn regex_literal() {
        let nodes = parse("if x =~ /hello/i\nend").unwrap();
        if let Node::If { condition, .. } = &nodes[0] {
            assert!(matches!(
                condition.as_ref(),
                Node::BinaryOp { op, .. } if op == "=~"
            ));
        }
    }

    // ── Interpolation ───────────────────────────────────────────────

    #[test]
    fn string_interpolation() {
        let nodes = parse("\"hello #{name}\"").unwrap();
        assert!(matches!(&nodes[0], Node::InterpolatedString { parts } if parts.len() == 2));
    }

    // ── Builtins ────────────────────────────────────────────────────

    #[test]
    fn builtin_without_parens() {
        let nodes = parse("puts \"hello\"").unwrap();
        assert!(
            matches!(&nodes[0], Node::FunctionCall { name, args } if name == "puts" && args.len() == 1)
        );
    }

    // ── Platform Blocks ─────────────────────────────────────────────

    #[test]
    fn platform_block() {
        let nodes = parse("macos\n  puts \"mac\"\nend").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::PlatformBlock { platform, body: Some(_), .. } if platform == "macos"
        ));
    }

    #[test]
    fn ps_block() {
        let nodes = parse("ps\n  Get-Process\nend").unwrap();
        assert!(matches!(
            &nodes[0],
            Node::PlatformBlock { platform, raw_body: Some(_), .. } if platform == "ps"
        ));
    }

    // ── Return ──────────────────────────────────────────────────────

    #[test]
    fn return_value() {
        let nodes = parse("return 42").unwrap();
        assert!(matches!(&nodes[0], Node::Return { value: Some(_) }));
    }

    #[test]
    fn return_bare() {
        let nodes = parse("return").unwrap();
        assert!(matches!(&nodes[0], Node::Return { value: None }));
    }

    // ── Loop Control ────────────────────────────────────────────────

    #[test]
    fn break_with_postfix() {
        let nodes = parse("break if done").unwrap();
        assert!(matches!(&nodes[0], Node::PostfixIf { .. }));
    }

    // ── Command Substitution ────────────────────────────────────────

    #[test]
    fn command_sub() {
        let nodes = parse("x = $(echo hello)").unwrap();
        if let Node::Assignment { value, .. } = &nodes[0] {
            assert!(matches!(value.as_ref(), Node::CommandSub { .. }));
        }
    }

    // ── Multiple Statements ─────────────────────────────────────────

    #[test]
    fn multiple_statements() {
        let nodes = parse("x = 1\ny = 2\nz = x + y").unwrap();
        assert_eq!(nodes.len(), 3);
    }

    #[test]
    fn semicolon_separator() {
        let nodes = parse("x = 1; y = 2").unwrap();
        assert_eq!(nodes.len(), 2);
    }

    // ── Property Assignment ─────────────────────────────────────────

    #[test]
    fn property_assignment() {
        let nodes = parse("self.name = \"Rush\"").unwrap();
        assert!(matches!(&nodes[0], Node::PropertyAssignment { .. }));
    }

    // ── Safe Navigation ─────────────────────────────────────────────

    #[test]
    fn safe_nav() {
        let nodes = parse("user&.name").unwrap();
        assert!(matches!(&nodes[0], Node::SafeNav { member, .. } if member == "name"));
    }
}
