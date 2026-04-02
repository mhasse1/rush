" Vim syntax file
" Language:    Rush (shell scripting language transpiling to PowerShell 7)
" Maintainer:  Rush Contributors
" Last Change: 2026-04-02
" URL:         https://github.com/rush-lang/rush

if exists("b:current_syntax")
  finish
endif

let s:cpo_save = &cpo
set cpo&vim

" Sync from start for accurate multi-line string highlighting
syn sync fromstart

" --------------------------------------------------------------------------
" Comments
" --------------------------------------------------------------------------
syn match   rushComment       "#.*$" contains=rushTodo,@Spell
syn keyword rushTodo          TODO FIXME XXX HACK NOTE BUG OPTIMIZE REVIEW contained

" --------------------------------------------------------------------------
" Keywords
" --------------------------------------------------------------------------
syn keyword rushConditional   if elsif else unless match
syn keyword rushRepeat        for while until loop
syn keyword rushKeyword       end def return do begin
syn keyword rushKeyword       case when in
syn keyword rushKeyword       next continue break
syn keyword rushKeyword       class attr self super enum
syn keyword rushException     try rescue ensure raise
syn keyword rushOperatorWord  and or not
syn keyword rushPlatform      macos linux win64 win32 ps ps5 isssh

" --------------------------------------------------------------------------
" Constants
" --------------------------------------------------------------------------
syn keyword rushBoolean       true false
syn keyword rushNil           nil

" --------------------------------------------------------------------------
" Builtin functions
" --------------------------------------------------------------------------
syn keyword rushBuiltin       puts print warn die ask sleep exit ping

" --------------------------------------------------------------------------
" Stdlib classes
" --------------------------------------------------------------------------
syn keyword rushType          File Dir Time

" --------------------------------------------------------------------------
" Numbers
" --------------------------------------------------------------------------
" Float must come before integer to match correctly
syn match   rushFloat         "\<\d\+\.\d\+\>"
syn match   rushInteger       "\<\d\+\>"

" --------------------------------------------------------------------------
" Symbols (:name)
" --------------------------------------------------------------------------
syn match   rushSymbol        ":\a\w*"

" --------------------------------------------------------------------------
" Operators
" --------------------------------------------------------------------------
syn match   rushOperator      "==\|!=\|<=\|>=\|=\~\|!\~\|<\|>"
syn match   rushOperator      "&&"
syn match   rushOperator      "||"
syn match   rushOperator      "+=\|-=\|="
syn match   rushOperator      "+\|-\|\*\|/\|%"
syn match   rushOperator      "\.\."
syn match   rushOperator      "&\."
syn match   rushOperator      "|"

" --------------------------------------------------------------------------
" Special variables
" --------------------------------------------------------------------------
" $? exit status
syn match   rushSpecialVar    "\$?"
" env.NAME environment variable access
syn match   rushEnvAccess     "\<env\>\.\w\+"

" --------------------------------------------------------------------------
" Command substitution $( ... )
" --------------------------------------------------------------------------
syn region  rushCmdSubst      matchgroup=rushCmdSubstDelim start="\$(" end=")" contains=TOP

" --------------------------------------------------------------------------
" Strings
" --------------------------------------------------------------------------
" Double-quoted strings with interpolation
syn region  rushString        start=+"+ skip=+\\\\\|\\"+ end=+"+
                              \ contains=rushInterpolation,rushEscape,rushCmdSubst

" Single-quoted strings (no interpolation)
syn region  rushStringRaw     start=+'+ skip=+\\\\\|\\'+ end=+'+
                              \ contains=rushEscapeSingle

" String interpolation #{expr}
syn region  rushInterpolation matchgroup=rushInterpolationDelim
                              \ start="#{" end="}"
                              \ contained contains=TOP

" Escape sequences in double-quoted strings
syn match   rushEscape        "\\[\\\"nrtab0]" contained
syn match   rushEscape        "\\u\x\{4}" contained

" Escape sequences in single-quoted strings (only \\ and \')
syn match   rushEscapeSingle  "\\[\\']" contained

" --------------------------------------------------------------------------
" Block parameters { |x| ... } and { |x, y| ... }
" --------------------------------------------------------------------------
syn match   rushBlockParam    "|\s*\w\+\(\s*,\s*\w\+\)*\s*|" contained containedin=rushBlock
syn region  rushBlock         matchgroup=rushBraces start="{" end="}"
                              \ contains=TOP transparent

" --------------------------------------------------------------------------
" Method calls after dot
" --------------------------------------------------------------------------
" Collection/enumeration methods
syn match   rushMethod        "\.\@<=\(each\|select\|reject\|map\|flat_map\|sort_by\)\>"
syn match   rushMethod        "\.\@<=\(first\|last\|count\)\>"
syn match   rushMethod        "\.\@<=\(any\|all\)?"
syn match   rushMethod        "\.\@<=\(group_by\|uniq\|reverse\|join\)\>"
syn match   rushMethod        "\.\@<=\(sort\|skip\|skip_while\|push\|compact\|flatten\)\>"

" String methods
syn match   rushMethod        "\.\@<=\(strip\|lstrip\|rstrip\|upcase\|downcase\|split\)\>"
syn match   rushMethod        "\.\@<=\(split_whitespace\|lines\|trim_end\)\>"
syn match   rushMethod        "\.\@<=\(start_with\|end_with\|empty\|nil\)?"
syn match   rushMethod        "\.\@<=\(ljust\|rjust\|replace\|sub\|gsub\|scan\|match\)\>"

" Conversion methods
syn match   rushMethod        "\.\@<=\(to_json\|to_csv\|to_i\|to_f\|to_s\)\>"
syn match   rushMethod        "\.\@<=\(to_currency\|to_filesize\|to_percent\)\>"
syn match   rushMethod        "\.\@<=include?"

" Numeric methods
syn match   rushMethod        "\.\@<=\(round\|abs\|times\)\>"

" Color methods (terminal output)
syn match   rushMethod        "\.\@<=\(red\|green\|blue\|cyan\|yellow\|magenta\|white\|gray\)\>"

" --------------------------------------------------------------------------
" Highlight links to standard groups
" --------------------------------------------------------------------------
hi def link rushComment              Comment
hi def link rushTodo                 Todo

hi def link rushConditional          Conditional
hi def link rushRepeat               Repeat
hi def link rushKeyword              Keyword
hi def link rushException            Exception
hi def link rushOperatorWord         Keyword
hi def link rushPlatform             PreProc

hi def link rushBoolean              Boolean
hi def link rushNil                  Constant

hi def link rushBuiltin              Function
hi def link rushType                 Type

hi def link rushInteger              Number
hi def link rushFloat                Float
hi def link rushSymbol               Constant

hi def link rushOperator             Operator

hi def link rushSpecialVar           Special
hi def link rushEnvAccess            Special
hi def link rushCmdSubst             PreProc
hi def link rushCmdSubstDelim        PreProc

hi def link rushString               String
hi def link rushStringRaw            String
hi def link rushEscape               SpecialChar
hi def link rushEscapeSingle         SpecialChar

hi def link rushInterpolation        Normal
hi def link rushInterpolationDelim   Special

hi def link rushBlockParam           Identifier
hi def link rushBraces               Delimiter

hi def link rushMethod               Function

let b:current_syntax = "rush"

let &cpo = s:cpo_save
unlet s:cpo_save
