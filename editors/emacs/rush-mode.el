;;; rush-mode.el --- Major mode for editing Rush scripts  -*- lexical-binding: t; -*-

;; Copyright (C) 2026 Rush Contributors

;; Author: Rush Contributors
;; Version: 1.0.0
;; Keywords: languages, rush, shell
;; URL: https://github.com/rush-lang/rush
;; Package-Requires: ((emacs "25.1"))

;; This file is not part of GNU Emacs.

;; This program is free software: you can redistribute it and/or modify
;; it under the terms of the GNU General Public License as published by
;; the Free Software Foundation, either version 3 of the License, or
;; (at your option) any later version.

;; This program is distributed in the hope that it will be useful,
;; but WITHOUT ANY WARRANTY; without even the implied warranty of
;; MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
;; GNU General Public License for more details.

;; You should have received a copy of the GNU General Public License
;; along with this program.  If not, see <https://www.gnu.org/licenses/>.

;;; Commentary:

;; Major mode for editing Rush shell scripts.  Rush is a shell scripting
;; language that transpiles to PowerShell 7, combining clean readable syntax
;; with the full power of the PowerShell/.NET ecosystem.
;;
;; Features:
;;   - Syntax highlighting for keywords, builtins, constants, strings,
;;     comments, numbers, symbols, operators, and method calls
;;   - String interpolation highlighting within double-quoted strings
;;   - Automatic indentation for block structures
;;   - Comment support (# to end of line)
;;   - Auto-association with .rush files
;;
;; Installation:
;;
;;   Add the directory containing this file to your `load-path' and then:
;;
;;     (require 'rush-mode)
;;
;;   Or with use-package:
;;
;;     (use-package rush-mode
;;       :load-path "/path/to/rush/editors/emacs")

;;; Code:

(require 'font-lock)

;;; ---- Customization --------------------------------------------------------

(defgroup rush nil
  "Major mode for editing Rush scripts."
  :prefix "rush-"
  :group 'languages)

(defcustom rush-indent-offset 2
  "Number of spaces for each indentation level in Rush code."
  :type 'integer
  :safe #'integerp
  :group 'rush)

(defcustom rush-indent-tabs-mode nil
  "If non-nil, use tabs for indentation in Rush code."
  :type 'boolean
  :safe #'booleanp
  :group 'rush)

;;; ---- Faces ----------------------------------------------------------------

(defface rush-method-call-face
  '((t :inherit font-lock-function-call-face))
  "Face for method calls after a dot in Rush code."
  :group 'rush)

(defface rush-symbol-face
  '((t :inherit font-lock-constant-face))
  "Face for symbol literals (:name) in Rush code."
  :group 'rush)

(defface rush-interpolation-face
  '((t :inherit font-lock-variable-name-face))
  "Face for string interpolation delimiters in Rush code."
  :group 'rush)

(defface rush-command-substitution-face
  '((t :inherit font-lock-preprocessor-face))
  "Face for command substitution $() in Rush code."
  :group 'rush)

(defface rush-special-variable-face
  '((t :inherit font-lock-variable-name-face :weight bold))
  "Face for special variables like $? in Rush code."
  :group 'rush)

(defface rush-env-access-face
  '((t :inherit font-lock-variable-name-face))
  "Face for environment variable access (env.NAME) in Rush code."
  :group 'rush)

;;; ---- Syntax Table ---------------------------------------------------------

(defvar rush-mode-syntax-table
  (let ((table (make-syntax-table)))
    ;; Comment: # to end of line
    (modify-syntax-entry ?# "<" table)
    (modify-syntax-entry ?\n ">" table)

    ;; Strings
    (modify-syntax-entry ?\" "\"" table)
    (modify-syntax-entry ?\' "\"" table)

    ;; Backslash is escape character inside strings
    (modify-syntax-entry ?\\ "\\" table)

    ;; Underscores and ? are word constituents (for identifiers like empty?, snake_case)
    (modify-syntax-entry ?_ "w" table)
    (modify-syntax-entry ?? "w" table)

    ;; Punctuation / operators
    (modify-syntax-entry ?+ "." table)
    (modify-syntax-entry ?- "." table)
    (modify-syntax-entry ?* "." table)
    (modify-syntax-entry ?/ "." table)
    (modify-syntax-entry ?% "." table)
    (modify-syntax-entry ?= "." table)
    (modify-syntax-entry ?< "." table)
    (modify-syntax-entry ?> "." table)
    (modify-syntax-entry ?! "." table)
    (modify-syntax-entry ?& "." table)
    (modify-syntax-entry ?| "." table)
    (modify-syntax-entry ?~ "." table)

    ;; Paired delimiters
    (modify-syntax-entry ?\( "()" table)
    (modify-syntax-entry ?\) ")(" table)
    (modify-syntax-entry ?\[ "(]" table)
    (modify-syntax-entry ?\] ")[" table)
    (modify-syntax-entry ?\{ "(}" table)
    (modify-syntax-entry ?\} "){" table)

    ;; Dot is punctuation (method separator)
    (modify-syntax-entry ?. "." table)

    ;; Colon is punctuation (for symbols :name)
    (modify-syntax-entry ?: "." table)

    ;; Dollar is punctuation (for $? and $())
    (modify-syntax-entry ?$ "." table)

    ;; Comma is punctuation
    (modify-syntax-entry ?, "." table)

    ;; Semicolon is punctuation
    (modify-syntax-entry ?\; "." table)

    table)
  "Syntax table for `rush-mode'.")

;;; ---- Keywords and Font Lock -----------------------------------------------

(defconst rush-keywords
  '("if" "elsif" "else" "end" "unless"
    "for" "in" "while" "until" "loop"
    "match" "case" "when"
    "def" "return"
    "try" "rescue" "ensure" "raise" "begin"
    "do" "next" "continue" "break"
    "class" "attr" "self" "super" "enum"
    "and" "or" "not")
  "Rush language keywords.")

(defconst rush-constants
  '("true" "false" "nil")
  "Rush language constants.")

(defconst rush-builtin-functions
  '("puts" "print" "warn" "die" "ask" "sleep" "exit" "ping")
  "Rush built-in functions.")

(defconst rush-stdlib-classes
  '("File" "Dir" "Time")
  "Rush standard library classes.")

(defconst rush-platform-keywords
  '("macos" "linux" "win64" "win32" "ps" "ps5" "isssh")
  "Rush platform block keywords.")

(defconst rush-dot-methods
  '("each" "select" "reject" "map" "flat_map" "sort_by"
    "first" "last" "count" "any?" "all?" "group_by"
    "uniq" "reverse" "join" "to_json" "to_csv"
    "include?" "sort" "skip" "skip_while" "push"
    "compact" "flatten"
    ;; String methods
    "strip" "lstrip" "rstrip" "upcase" "downcase"
    "split" "split_whitespace" "lines" "trim_end"
    "start_with?" "end_with?" "empty?" "nil?"
    "ljust" "rjust" "replace" "sub" "gsub" "scan" "match"
    ;; Numeric methods
    "round" "abs" "times" "to_currency" "to_filesize"
    "to_percent" "to_i" "to_f" "to_s"
    ;; Color methods
    "red" "green" "blue" "cyan" "yellow" "magenta" "white" "gray")
  "Rush methods called with dot notation.")

(defconst rush-keywords-regexp
  (concat "\\_<" (regexp-opt rush-keywords t) "\\_>")
  "Regexp matching Rush keywords.")

(defconst rush-constants-regexp
  (concat "\\_<" (regexp-opt rush-constants t) "\\_>")
  "Regexp matching Rush constants.")

(defconst rush-builtin-functions-regexp
  (concat "\\_<" (regexp-opt rush-builtin-functions t) "\\_>")
  "Regexp matching Rush built-in functions.")

(defconst rush-stdlib-classes-regexp
  (concat "\\_<" (regexp-opt rush-stdlib-classes t) "\\_>")
  "Regexp matching Rush standard library classes.")

(defconst rush-platform-keywords-regexp
  (concat "\\_<" (regexp-opt rush-platform-keywords t) "\\_>")
  "Regexp matching Rush platform block keywords.")

(defconst rush-dot-methods-regexp
  (concat "\\." (regexp-opt rush-dot-methods t) "\\_>")
  "Regexp matching Rush dot-method calls.")

(defconst rush-font-lock-keywords
  `(
    ;; Comments are handled by syntax table, no rule needed here.

    ;; Keywords
    (,rush-keywords-regexp 1 font-lock-keyword-face)

    ;; Constants: true, false, nil
    (,rush-constants-regexp 1 font-lock-constant-face)

    ;; Function definitions: def name
    ("\\_<def\\_>[ \t]+\\([a-zA-Z_][a-zA-Z0-9_]*\\)"
     1 font-lock-function-name-face)

    ;; Built-in functions (when used as a call, not after a dot)
    (,(concat "\\(?:^\\|[^.]\\)" rush-builtin-functions-regexp) 1 font-lock-builtin-face)

    ;; Standard library classes: File, Dir, Time
    (,rush-stdlib-classes-regexp 1 font-lock-type-face)

    ;; Platform block keywords: macos, linux, win64, ps, ps5, etc.
    (,rush-platform-keywords-regexp 1 font-lock-preprocessor-face)

    ;; Dot-method calls: .each, .select, .map, etc.
    (,rush-dot-methods-regexp 1 'rush-method-call-face)

    ;; Symbol literals: :name, :foo_bar
    ("\\_<:\\([a-zA-Z_][a-zA-Z0-9_]*\\)"
     0 'rush-symbol-face)

    ;; Special variable: $?
    ("\\$\\?" 0 'rush-special-variable-face)

    ;; Command substitution: $(...)
    ("\\$((" 0 'rush-command-substitution-face)

    ;; Environment access: env.NAME
    ("\\_<env\\>\\.\\([a-zA-Z_][a-zA-Z0-9_]*\\)"
     0 'rush-env-access-face)

    ;; Numeric literals: integers and floats
    ("\\_<[0-9]+\\(?:\\.[0-9]+\\)?\\_>"
     0 font-lock-constant-face)

    ;; Block parameters: { |x| ... } or { |x, y| ... }
    ("{[ \t]*|\\([^|]*\\)|"
     1 font-lock-variable-name-face)

    ;; Operators (multi-character)
    ("\\(==\\|!=\\|<=\\|>=\\|=~\\|!~\\|&&\\|||\\|\\.\\.\\|&\\.\\|+=\\|-=\\)"
     0 font-lock-operator-face nil)
    )
  "Font-lock keywords for Rush mode.")

;;; ---- String Interpolation Highlighting ------------------------------------

(defun rush-syntax-propertize (start end)
  "Apply syntax properties to Rush code between START and END.
Handles string interpolation #{...} inside double-quoted strings
and command substitution $(...) markers."
  (goto-char start)
  ;; Handle string interpolation: #{expr} inside double-quoted strings
  (while (re-search-forward "#{" end t)
    (let ((beg (match-beginning 0)))
      (when (rush--in-double-quoted-string-p beg)
        ;; Mark #{ as punctuation so it doesn't confuse the syntax parser
        (put-text-property beg (1+ beg) 'syntax-table '(1 . nil))
        (put-text-property (1+ beg) (+ beg 2) 'syntax-table '(1 . nil))
        ;; Find matching closing brace
        (let ((depth 1)
              (pos (point)))
          (while (and (< pos end) (> depth 0))
            (cond
             ((eq (char-after pos) ?{) (setq depth (1+ depth)))
             ((eq (char-after pos) ?}) (setq depth (1- depth))))
            (setq pos (1+ pos)))
          (when (= depth 0)
            ;; Mark the closing } as punctuation
            (put-text-property (1- pos) pos 'syntax-table '(1 . nil))))))))

(defun rush--in-double-quoted-string-p (pos)
  "Return non-nil if POS is inside a double-quoted string."
  (save-excursion
    (let ((state (syntax-ppss pos)))
      (and (nth 3 state)               ; inside a string
           (eq (nth 3 state) ?\")))))   ; it is a double-quoted string

;;; ---- Indentation ----------------------------------------------------------

(defconst rush-block-start-regexp
  (concat "\\_<"
          (regexp-opt '("if" "unless" "for" "while" "until" "loop"
                        "def" "begin" "case" "match" "do"
                        "try" "class"
                        "macos" "linux" "win64" "win32" "ps" "ps5")
                      t)
          "\\_>")
  "Regexp matching keywords that open a new indentation block.")

(defconst rush-block-mid-regexp
  (concat "\\_<"
          (regexp-opt '("else" "elsif" "when" "rescue" "ensure") t)
          "\\_>")
  "Regexp matching keywords that continue a block at the same level.")

(defconst rush-block-end-regexp
  "\\_<end\\_>"
  "Regexp matching the keyword that closes a block.")

(defun rush-indent-line ()
  "Indent the current line as Rush code."
  (interactive)
  (let ((indent (rush--calculate-indent))
        (offset (- (current-column) (current-indentation))))
    (indent-line-to indent)
    ;; Preserve cursor position relative to indentation
    (when (> offset 0)
      (move-to-column (+ indent offset)))))

(defun rush--calculate-indent ()
  "Calculate the proper indentation for the current line."
  (save-excursion
    (beginning-of-line)
    (let ((current-line (rush--current-line-trimmed))
          (prev-indent 0)
          (prev-line nil))
      ;; First line in buffer: no indentation
      (if (bobp)
          0
        ;; Find the previous non-blank, non-comment line
        (setq prev-line (rush--previous-code-line))
        (if (null prev-line)
            0
          (setq prev-indent (car prev-line))
          (let ((prev-text (cdr prev-line))
                (indent prev-indent))
            ;; Increase indent if previous line opened a block
            (when (rush--line-opens-block-p prev-text)
              (setq indent (+ indent rush-indent-offset)))
            ;; Decrease indent if current line closes or continues a block
            (when (or (rush--line-closes-block-p current-line)
                      (rush--line-continues-block-p current-line))
              (setq indent (- indent rush-indent-offset)))
            ;; Never indent below zero
            (max 0 indent)))))))

(defun rush--current-line-trimmed ()
  "Return the current line contents, trimmed of leading/trailing whitespace."
  (string-trim (buffer-substring-no-properties
                (line-beginning-position) (line-end-position))))

(defun rush--previous-code-line ()
  "Find the previous non-blank, non-comment-only line.
Return a cons cell (INDENTATION . TRIMMED-TEXT), or nil if none found."
  (save-excursion
    (forward-line -1)
    (while (and (not (bobp))
                (or (looking-at-p "^[ \t]*$")
                    (looking-at-p "^[ \t]*#")))
      (forward-line -1))
    (if (and (bobp)
             (or (looking-at-p "^[ \t]*$")
                 (looking-at-p "^[ \t]*#")))
        nil
      (cons (current-indentation)
            (rush--current-line-trimmed)))))

(defun rush--line-opens-block-p (line)
  "Return non-nil if LINE opens a new indentation block."
  (and (not (rush--comment-only-p line))
       (let ((code (rush--strip-comment line)))
         (or
          ;; Block-opening keywords at start of line or after assignment
          (string-match-p (concat "\\(?:^\\|=[ \t]*\\)"
                                  rush-block-start-regexp)
                          code)
          ;; Block-mid keywords also open a sub-block
          (string-match-p (concat "^" rush-block-mid-regexp) code)
          ;; Line ending with do (inline block)
          (string-match-p "\\_<do\\_>[ \t]*$" code)
          ;; Line ending with opening brace for block: { |x|
          (string-match-p "{[ \t]*|[^|]*|[ \t]*$" code)))))

(defun rush--line-closes-block-p (line)
  "Return non-nil if LINE closes a block."
  (and (not (rush--comment-only-p line))
       (string-match-p (concat "^" rush-block-end-regexp) line)))

(defun rush--line-continues-block-p (line)
  "Return non-nil if LINE continues a block (else, elsif, when, rescue, ensure)."
  (and (not (rush--comment-only-p line))
       (string-match-p (concat "^" rush-block-mid-regexp) line)))

(defun rush--comment-only-p (line)
  "Return non-nil if LINE is a comment-only line."
  (string-match-p "^[ \t]*#" line))

(defun rush--strip-comment (line)
  "Remove trailing comment from LINE, respecting strings."
  (let ((result "")
        (i 0)
        (len (length line))
        (in-string nil))
    (while (< i len)
      (let ((ch (aref line i)))
        (cond
         ;; Toggle double-quote string state
         ((and (eq ch ?\") (not (eq in-string ?\')))
          (if (eq in-string ?\")
              (setq in-string nil)
            (setq in-string ?\"))
          (setq result (concat result (string ch))))
         ;; Toggle single-quote string state
         ((and (eq ch ?\') (not (eq in-string ?\")))
          (if (eq in-string ?\')
              (setq in-string nil)
            (setq in-string ?\'))
          (setq result (concat result (string ch))))
         ;; Backslash escape inside strings
         ((and in-string (eq ch ?\\) (< (1+ i) len))
          (setq result (concat result (substring line i (+ i 2))))
          (setq i (1+ i)))
         ;; Comment start outside strings
         ((and (not in-string) (eq ch ?#))
          (setq i len))  ; break
         ;; Normal character
         (t
          (setq result (concat result (string ch))))))
      (setq i (1+ i)))
    (string-trim-right result)))

;;; ---- Keymap ---------------------------------------------------------------

(defvar rush-mode-map
  (let ((map (make-sparse-keymap)))
    map)
  "Keymap for `rush-mode'.")

;;; ---- Imenu Support --------------------------------------------------------

(defvar rush-imenu-generic-expression
  `(("Functions" "^[ \t]*\\_<def\\_>[ \t]+\\([a-zA-Z_][a-zA-Z0-9_]*\\)" 1))
  "Imenu generic expression for Rush mode.
Finds function definitions.")

;;; ---- Mode Definition ------------------------------------------------------

;;;###autoload
(define-derived-mode rush-mode prog-mode "Rush"
  "Major mode for editing Rush shell scripts.

Rush is a shell scripting language that transpiles to PowerShell 7.
It combines clean, readable syntax with the full power of the PowerShell
and .NET ecosystem.

\\{rush-mode-map}"
  :syntax-table rush-mode-syntax-table
  :group 'rush

  ;; Comments
  (setq-local comment-start "# ")
  (setq-local comment-end "")
  (setq-local comment-start-skip "#+ *")

  ;; Indentation
  (setq-local indent-line-function #'rush-indent-line)
  (setq-local indent-tabs-mode rush-indent-tabs-mode)
  (setq-local tab-width rush-indent-offset)

  ;; Font lock
  (setq-local font-lock-defaults
              '(rush-font-lock-keywords
                nil    ; keywords-only (nil = also fontify strings/comments)
                nil    ; case-fold (nil = case-sensitive)
                nil    ; syntax-alist
                ))

  ;; String interpolation via syntax-propertize
  (setq-local syntax-propertize-function #'rush-syntax-propertize)

  ;; Electric indentation
  (setq-local electric-indent-chars
              (append '(?\n) (if (boundp 'electric-indent-chars)
                                 electric-indent-chars
                               nil)))

  ;; Paragraph and filling
  (setq-local paragraph-start (concat "$\\|" page-delimiter))
  (setq-local paragraph-separate paragraph-start)
  (setq-local paragraph-ignore-fill-prefix t)

  ;; Imenu
  (setq-local imenu-generic-expression rush-imenu-generic-expression)

  ;; Beginning/end of defun
  (setq-local beginning-of-defun-function #'rush-beginning-of-defun)
  (setq-local end-of-defun-function #'rush-end-of-defun))

;;; ---- Defun Navigation -----------------------------------------------------

(defun rush-beginning-of-defun (&optional arg)
  "Move backward to the beginning of a Rush function definition.
With ARG, move backward that many functions."
  (interactive "^p")
  (setq arg (or arg 1))
  (if (> arg 0)
      (re-search-backward "^[ \t]*\\_<def\\_>" nil t arg)
    (re-search-forward "^[ \t]*\\_<def\\_>" nil t (- arg))))

(defun rush-end-of-defun (&optional arg)
  "Move forward to the end of a Rush function definition.
With ARG, move forward that many function ends."
  (interactive "^p")
  (setq arg (or arg 1))
  ;; From current position, find the matching `end' for `def'
  (dotimes (_ arg)
    (let ((depth 0)
          (found nil))
      ;; If we're on a def line, move past it
      (when (looking-at-p "^[ \t]*\\_<def\\_>")
        (setq depth 1)
        (forward-line 1))
      ;; Scan forward for matching end
      (while (and (not (eobp)) (not found))
        (let ((line (rush--current-line-trimmed)))
          (cond
           ((string-match-p (concat "^" rush-block-start-regexp) line)
            (setq depth (1+ depth)))
           ((string-match-p (concat "^" rush-block-end-regexp) line)
            (if (<= depth 1)
                (progn
                  (forward-line 1)
                  (setq found t))
              (setq depth (1- depth))
              (forward-line 1)))
           (t (forward-line 1))))))))

;;; ---- File Association -----------------------------------------------------

;;;###autoload
(add-to-list 'auto-mode-alist '("\\.rush\\'" . rush-mode))

(provide 'rush-mode)

;;; rush-mode.el ends here
