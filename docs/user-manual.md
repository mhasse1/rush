# Rush User Manual

> **Version 0.2.0 (alpha)** — A modern shell with Ruby-inspired syntax on PowerShell 7

---

## Table of Contents

1. [Overview](#overview)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [The REPL](#the-repl)
5. [Shell Features](#shell-features)
6. [Scripting Language](#scripting-language)
7. [Standard Library](#standard-library)
8. [Built-in Commands](#built-in-commands)
9. [The `ls` Builtin](#the-ls-builtin)
10. [Command Translations](#command-translations)
11. [Built-in Variables](#built-in-variables)
12. [Tips & Tricks](#tips--tricks)

---

## Overview

Rush is a Unix shell that combines Bash's pipeline and process model with Ruby's clean syntax. Under the hood, Rush code transpiles to PowerShell 7, giving you access to the full .NET runtime while writing natural, readable commands.

**Design principles:**

- Shell commands are first-class — `ls -la /tmp` just works
- No sigils for variables — `name = "mark"`, not `$name`
- String interpolation uses `#{expr}` (Ruby-style)
- Block keywords use `end`, not braces — `if`/`end`, `def`/`end`
- Braces `{ }` are for lambdas and blocks only
- Pipelines work for both shell commands and objects

---

## Installation

### Prerequisites

- .NET 8 SDK
- macOS (arm64) or Linux

### Build from Source

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
dotnet publish -c Release -r osx-arm64
```

### Install

```bash
./install.sh
```

This copies the self-contained binary to `/usr/local/lib/rush/` and creates a symlink at `/usr/local/bin/rush`. No runtime dependencies needed.

### Set as Login Shell (Optional)

```bash
chsh -s /usr/local/bin/rush
```

### Command-Line Usage

```
rush                     Start interactive shell
rush script.rush         Execute a Rush script
rush -c 'command'        Execute command and exit
rush --version (-v)      Show version
rush --help (-h)         Show help
```

---

## Configuration

### Config Directory

All configuration lives in `~/.config/rush/`:

| File | Purpose |
|------|---------|
| `config.json` | Settings (edit mode, aliases, theme, history size) |
| `init.rush` | Startup script — transpiled through Rush engine |
| `history` | Persistent command history |
| `sync.json` | Config sync settings |

### config.json

Created automatically on first run. All settings with their defaults:

```json
{
  "editMode": "vi",
  "aliases": {},
  "promptFormat": "default",
  "historySize": 500,
  "theme": "auto"
}
```

| Setting | Values | Description |
|---------|--------|-------------|
| `editMode` | `"vi"`, `"emacs"` | Line editing mode |
| `aliases` | `{ "ll": "ls -la" }` | Command aliases |
| `promptFormat` | `"default"` | Prompt style |
| `historySize` | `10`–`∞` (default `500`) | Max history entries |
| `theme` | `"auto"`, `"dark"`, `"light"` | Color theme (`auto` detects terminal) |

### Startup Script

**`init.rush`** runs on every shell launch, fully transpiled through the Rush engine. Everything goes here — exports, aliases, functions, and prompt customization:

```rush
# ~/.config/rush/init.rush
export PATH="/opt/homebrew/bin:$PATH"
export EDITOR=vim

alias ll='ls -la'
alias g='git'

def rush_prompt()
  time = Time.now.ToString("HH:mm")
  dir = pwd
  "#{time} #{dir} > "
end
```

A commented default is created on first run.

### Reloading

Run `reload` to re-read all config files without restarting Rush.

---

## The REPL

### Prompt

The default prompt shows:

```
✓ 14:32  mark@macbook  rush/src  main*
  █
```

Components (left to right):
- **Exit status**: ✓ (success) or ✗ + exit code (failure)
- **Time**: 24-hour HH:mm
- **User@host**: highlights differently when connected via SSH
- **Path**: shortened to last 2 directory levels (`~` for home)
- **Git branch**: with `*` suffix if there are uncommitted changes

### Vi Mode (Default)

Rush defaults to vi-style line editing.

**Insert mode** (type normally):

| Key | Action |
|-----|--------|
| `Esc` | Switch to normal mode |
| `Tab` | Complete paths and commands |
| `Ctrl+C` | Cancel current line |
| `Ctrl+R` | Reverse history search |

**Normal mode** (after pressing Esc):

| Key | Action |
|-----|--------|
| `h` / `l` | Move left / right |
| `w` / `b` / `e` | Word forward / back / end |
| `0` / `$` | Beginning / end of line |
| `i` / `a` | Insert before / after cursor |
| `I` / `A` | Insert at beginning / end of line |
| `x` | Delete character |
| `d` + motion | Delete (e.g., `dw` delete word, `d$` delete to end) |
| `c` + motion | Change (delete + enter insert mode) |
| `D` | Delete to end of line |
| `C` | Change to end of line |
| `y` + motion | Yank (copy) |
| `p` | Paste |
| `u` | Undo |
| `f`/`F` + char | Find character forward / backward |
| `j` / `k` | Next / previous history entry |
| `3w` | Count prefix — move 3 words forward |

### Emacs Mode

Switch with `set emacs`.

| Key | Action |
|-----|--------|
| `Ctrl+A` | Beginning of line |
| `Ctrl+E` | End of line |
| `Ctrl+K` | Kill to end of line |
| `Ctrl+U` | Kill to beginning of line |
| `Ctrl+W` | Kill previous word |
| `Ctrl+Y` | Yank (paste) killed text |
| `Alt+B` | Back one word |
| `Alt+F` | Forward one word |
| `Alt+D` | Delete word forward |

### Tab Completion

Tab completes based on context:

- **First token**: commands (builtins, translated commands, PATH binaries)
- **After `cd` / `pushd`**: directories only
- **After `-`**: flags (context-aware per command)
- **After `$`**: environment variables
- **Otherwise**: files and directories (with `/` suffix for dirs)

Press Tab multiple times to cycle through matches.

### History

Commands are saved to `~/.config/rush/history` (persistent across sessions).

| Command | Action |
|---------|--------|
| `history` | Show last 50 entries (numbered) |
| `history -c` | Clear all history |
| `Ctrl+R` | Interactive reverse search |
| `!N` | Run command number N |
| `!!` | Repeat last command |
| `!$` | Last argument of previous command |
| `j` / `k` (vi normal) | Browse history |

### Multi-Line Editing

Block-opening keywords automatically continue to the next line:

```rush
if count > 10
  puts "many"       # ← continuation line
end                  # ← block closes
```

This works for `if`, `unless`, `for`, `while`, `until`, `loop`, `def`, `begin`, `try`, `case`, `match`, `class`, `enum`.

### Syntax Highlighting

Rush highlights your input as you type: keywords, strings, numbers, operators, commands, and comments are each colored distinctly. The theme (`"auto"`, `"dark"`, `"light"` in config.json) controls the color palette.

### Special Keys

| Key | Action |
|-----|--------|
| `Ctrl+C` | Cancel current line (prints `^C`, new prompt) |
| `Ctrl+D` | Exit Rush (on empty line) |
| `Ctrl+R` | Reverse incremental search |
| `\` (trailing) | Line continuation |

---

## Shell Features

### Pipelines

Chain commands with `|`. Objects flow through the pipeline:

```rush
ls /var/log | grep ".log" | head 5
ps | where CPU > 10 | select ProcessName, CPU | as table
```

### Redirections

```rush
ls > files.txt          # Overwrite
echo "line" >> log.txt  # Append
sort < input.txt        # Read from file
cmd 2> errors.txt       # Redirect stderr
cmd 2>&1                # Merge stderr into stdout
```

### Brace Expansion

```rush
echo {a,b,c}            # → a b c
touch file_{1,2,3}.txt  # Creates file_1.txt file_2.txt file_3.txt
echo {1..5}             # → 1 2 3 4 5
```

### Tilde Expansion

```rush
cd ~                    # Home directory
ls ~/Documents          # Home + path
cd ~otheruser           # Another user's home
```

### Globbing

```rush
ls *.txt                # All .txt files
ls file?.log            # Single character wildcard
ls [abc]*.rs            # Character class
```

### Command Substitution

```rush
branch = $(git rev-parse --abbrev-ref HEAD)
echo "on branch: $(git branch --show-current)"
```

### Process Substitution

```rush
diff <(ls dir1) <(ls dir2)
```

### Arithmetic Expansion

```rush
echo $(( 2 + 3 ))       # → 5
echo $(( 10 * 4 / 2 ))  # → 20
```

### Heredocs

```rush
cat <<EOF
Hello #{name},
Welcome to Rush.
EOF
```

### Background Jobs

```rush
sleep 60 &              # Run in background
jobs                    # List background jobs
fg %1                   # Bring job 1 to foreground
kill %1                 # Kill job 1
wait                    # Wait for all background jobs
wait %1                 # Wait for specific job
```

### Chain Operators

```rush
make && make install    # Run second only if first succeeds
cmd || echo "failed"    # Run second only if first fails
cmd1 ; cmd2 ; cmd3      # Run all regardless of exit status
```

### Dot Notation in Pipelines

Access object properties directly in pipeline:

```rush
ps | .ProcessName       # Extract property
json config.json | .settings.theme
```

---

## Scripting Language

### Variables & Assignment

```rush
name = "world"
count = 42
files = ls /tmp         # Capture command output
today = Time.now
```

Compound assignment:

```rush
count += 1
total -= 5
score *= 2
ratio /= 100
```

Multiple assignment:

```rush
a, b, c = 1, 2, 3
first, last = "Alice", "Smith"
```

### Strings

**Double-quoted** — with `#{expr}` interpolation:

```rush
greeting = "hello #{name}"
puts "there are #{items.count} items"
puts "path: #{env.HOME}/documents"
```

**Single-quoted** — literal (no interpolation):

```rush
pattern = 'no #{interpolation} here'
```

**Escape sequences** (in double-quoted strings):

| Escape | Character |
|--------|-----------|
| `\n` | Newline |
| `\t` | Tab |
| `\\` | Backslash |
| `\"` | Double quote |

**Heredocs** for multi-line strings:

```rush
message = <<END
Dear #{name},
This is a multi-line
message with interpolation.
END
```

### String Methods

```rush
"hello".upcase              # → "HELLO"
"HELLO".downcase            # → "hello"
"  hi  ".strip              # → "hi"
"  hi  ".lstrip             # → "hi  "
"  hi  ".rstrip             # → "  hi"

"a,b,c".split(",")         # → ["a", "b", "c"]
"hello world".split_whitespace  # → ["hello", "world"]
"line1\nline2".lines        # → ["line1", "line2"]

"hi".ljust(10)              # → "hi        "
"hi".rjust(10)              # → "        hi"
"100%".trim_end("%")        # → "100"

"hello".start_with?("he")  # → true
"hello".end_with?("lo")    # → true
"hello".include?("ell")    # → true
"".empty?                   # → true

"hello".replace("l", "r")  # → "herro"
"hello".to_i                # → 0
"42".to_i                   # → 42
"3.14".to_f                 # → 3.14
42.to_s                     # → "42"
nil.nil?                    # → true
"hello".nil?                # → false
```

**Color methods** — for terminal output:

```rush
"success".green
"error".red
"warning".yellow
"info".cyan
# Colors: .red, .green, .blue, .cyan, .yellow, .magenta, .white, .gray
```

### Regex

**Literals and matching:**

```rush
name =~ /^test/             # Match (returns true/false)
name !~ /admin/             # Not match

line.sub(/^#\s*/, "")       # Replace first match
text.gsub(/\t/, "  ")       # Replace all matches
path.scan(/[^\/]+/)         # All matches as array
line.match(/^(\w+)=(.*)$/)  # Match with captures
```

### Numbers

```rush
(-5).abs                    # → 5
3.14159.round(2)            # → 3.14
```

**Formatting methods:**

```rush
14.25.to_currency           # → "$14.25"
14.25.to_currency(pad: 10)  # → "    $14.25"
1048576.to_filesize         # → "1.0 MB"
0.8734.to_percent           # → "87.3%"
0.8734.to_percent(decimals: 2)  # → "87.34%"
```

**Duration literals:**

```rush
2.hours                     # TimeSpan
30.minutes
45.seconds
7.days
```

### Control Flow

**if / elsif / else / end:**

```rush
if count > 10
  puts "many"
elsif count > 0
  puts "some"
else
  puts "none"
end
```

**unless / end:**

```rush
unless quiet
  puts "verbose output"
end
```

**Postfix form** (one-liners):

```rush
puts "hello" if verbose
puts "skipping" unless debug
```

**Comparison operators:**

| Rush | Meaning |
|------|---------|
| `==` | Equal |
| `!=` | Not equal |
| `>` | Greater than |
| `<` | Less than |
| `>=` | Greater or equal |
| `<=` | Less or equal |

**Logical operators:**

| Rush | Also |
|------|------|
| `&&` | `and` |
| `\|\|` | `or` |
| `!` | `not` |

**Safe navigation:**

```rush
user&.name                  # Returns nil if user is nil, otherwise user.name
```

**Pattern matching:**

```rush
case status
when "ok"
  puts "all good"
when "error"
  puts "something broke"
else
  puts "unknown: #{status}"
end
```

`match` / `when` / `end` also works as an alias for `case`.

### Loops

**for / in / end:**

```rush
for file in Dir.files(".")
  puts file.Name
end

for i in 1..5
  puts i                    # Prints 1 through 5
end

for i in 1...5
  puts i                    # Prints 1 through 4 (exclusive end)
end
```

**while / end:**

```rush
while retries > 0
  retries -= 1
  # try something
end
```

**until / end:**

```rush
until done
  # keep working
end
```

**loop / end** (infinite):

```rush
loop
  line = ask "command> "
  break if line == "quit"
  # process line
end
```

**Loop control:**

```rush
next if line.empty?         # Skip this iteration
break if count > 100        # Exit the loop
```

`continue` also works as an alias for `next`.

**Iteration with `.times`:**

```rush
5.times { |i|
  puts "iteration #{i}"    # 0, 1, 2, 3, 4
}
```

### Functions

```rush
def greet(name, greeting = "hello")
  puts "#{greeting}, #{name}!"
end

greet("world")              # → "hello, world!"
greet("world", "hey")       # → "hey, world!"
```

**Named arguments** (Ruby-style colon syntax):

```rush
def deploy(env, dry_run: false, verbose: true)
  puts "deploying to #{env}" unless dry_run
end

deploy("staging", dry_run: true)
deploy("production", verbose: false)
```

**Return values:**

```rush
def double(n)
  n * 2                     # Implicit return (last expression)
end

def validate(input)
  return "invalid" if input.empty?
  input.strip
end

result = double(21)         # → 42
```

### Classes

Define classes with `class`/`end`, attributes with `attr`, and constructors with `def initialize`:

```rush
class Greeter
  attr name

  def initialize(name)
    self.name = name
  end

  def greet
    return "Hello, " + self.name
  end
end

g = Greeter.new("World")
puts g.greet()               # → "Hello, World"
```

**Key concepts:**

- **`attr`** declares instance attributes (comma-separated for multiple)
- **`attr name: String`** — optional type annotation (`String`, `Int`, `Bool`, `Float`, or custom class)
- **`self.prop`** accesses instance properties (required — no bare name shortcuts)
- **`def initialize(...)`** defines the constructor
- **`ClassName.new(args)`** creates a new instance
- **`ClassName.new(name: value)`** — named args are reordered to match constructor params
- Methods without `return` are void; methods with `return` produce values

**Constructor defaults:**

```rush
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def increment
    self.value = self.value + 1
  end

  def get_value
    return self.value
  end
end

c = Counter.new()             # Uses default start: 0
c.increment()
c.increment()
puts c.get_value()            # → 2

d = Counter.new(10)           # Explicit start value
d.increment()
puts d.get_value()            # → 11
```

**Typed attributes and named args:**

```rush
class Person
  attr name: String, age: Int

  def initialize(name, age)
    self.name = name
    self.age = age
  end
end

p = Person.new(age: 30, name: "Alice")  # named args, any order
puts p.name                              # → "Alice"
```

**Inheritance:**

Use `<` to inherit from a parent class. Call `super(args)` in the constructor to pass arguments to the parent, and `super.method()` to call a parent method from an override:

```rush
class Animal
  attr name

  def initialize(name)
    self.name = name
  end

  def speak
    puts "..."
  end
end

class Dog < Animal
  attr breed

  def initialize(name, breed)
    super(name)
    self.breed = breed
  end

  def speak
    super.speak()
    puts "Woof!"
  end
end

d = Dog.new("Rex", "Lab")
puts d.name                   # → "Rex"
d.speak()                     # → "..." then "Woof!"
```

Inheritance rules:
- The parent class must be defined before the child class in the same script
- `super(args)` in a constructor passes arguments to the parent constructor
- `super.method()` calls the parent's version of a method
- Child classes inherit all attributes and methods from the parent

**Static methods:**

Define class-level methods with `def self.method_name`. Call them with `ClassName.method()` — no instance needed:

```rush
class MathHelper
  def self.add(a, b)
    return a + b
  end

  def self.pi
    return 3.14159
  end
end

puts MathHelper.add(2, 3)     # → 5
puts MathHelper.pi()          # → 3.14159
```

A class can have both instance methods and static methods:

```rush
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def increment
    self.value = self.value + 1
  end

  def self.create_pair
    return [Counter.new(0), Counter.new(100)]
  end
end

pair = Counter.create_pair()
```

### Enums

Define enumerations with `enum`/`end`. Members are listed one per line, optionally with explicit integer values:

```rush
enum Color
  red
  green
  blue
end

enum Priority
  low = 1
  medium = 5
  high = 10
end
```

Access members with `EnumName.member`:

```rush
favorite = Color.blue
puts favorite                 # → "Blue"

if favorite == Color.blue
  puts "good choice"
end
```

Enum rules:
- Member names are lowercased in Rush but PascalCased in output
- Members without explicit values are auto-numbered (starting from 0)
- Enums support assignment and comparison (`==`, `!=`)

### Blocks & Iteration

**Block syntax** — `{ |args| }` or `do |args| ... end`:

```rush
files.each { |f| puts f.Name }

names = users.map { |u| u.name }

big_files = files.select { |f| f.Length > 1mb }

small_files = files.reject { |f| f.Length > 1mb }
```

**Available iteration methods:**

| Method | Description |
|--------|-------------|
| `.each { \|x\| }` | Iterate over each element |
| `.map { \|x\| }` | Transform each element |
| `.select { \|x\| }` | Keep elements where block is true |
| `.reject { \|x\| }` | Remove elements where block is true |
| `.sort_by { \|x\| }` | Sort by block result |
| `.group_by { \|x\| }` | Group into hash by block result |
| `.flat_map { \|x\| }` | Map and flatten one level |
| `.any? { \|x\| }` | True if any element matches |
| `.all? { \|x\| }` | True if all elements match |
| `.count` | Number of elements |
| `.first(n)` | First n elements |
| `.last(n)` | Last n elements |
| `.skip(n)` | Skip first n elements |
| `.uniq` | Remove duplicates |
| `.reverse` | Reverse order |

**Method chaining:**

```rush
Dir.files("/var/log", recursive: true)
  .select { |f| f.Name =~ /\.log$/ }
  .sort_by { |f| f.Length }
  .reverse
  .first(10)
  .each { |f| puts "#{f.Name.ljust(30)} #{f.Length.to_filesize}" }
```

**Chained output:**

```rush
Dir.files(".", recursive: true).print
[1, 2, 3].map { |x| x * 2 }.puts
```

### Error Handling

```rush
begin
  data = File.read_json("config.json")
rescue => e
  warn "failed to read config: #{e}"
  data = {}
end
```

**With ensure (finally):**

```rush
try
  file = File.read("important.txt")
rescue IOError => e
  warn "IO error: #{e}"
rescue => e
  warn "unexpected error: #{e}"
ensure
  puts "cleanup done"
end
```

**Exit status checking:**

```rush
git pull origin main
if $?.failed?
  warn "git pull failed with code #{$?.code}"
  exit 1
end

curl -s https://api.example.com/health
puts "API is up" if $?.ok?
```

### Data Structures

**Arrays:**

```rush
items = [1, 2, 3, "four", 5.0]
items[0]                    # → 1
items[-1]                   # → 5.0
items.count                 # → 5
```

**Hashes:**

```rush
config = { name: "rush", version: "0.2.0", debug: false }
config[:name]               # → "rush"
config.name                 # → "rush" (dot access)
```

**Symbols:**

```rush
status = :ok
mode = :verbose
```

**Ranges:**

```rush
1..10                       # Inclusive: 1 through 10
1...10                      # Exclusive: 1 through 9
```

---

## Standard Library

Receivers are case-insensitive: `Dir.files()` and `dir.files()` both work.

### File

```rush
File.read("path")           # Read entire file as string
File.read_lines("path")     # Read as array of lines
File.read_json("path")      # Parse JSON file into object
File.read_csv("path")       # Parse CSV file into array of objects

File.write("path", content) # Write string (overwrite)
File.append("path", text)   # Append string to file

File.exist?("path")         # Check if file exists → true/false
File.delete("path")         # Delete file
File.size("path")           # File size in bytes
```

**Examples:**

```rush
# Read and process JSON config
config = File.read_json("package.json")
puts "Project: #{config.name} v#{config.version}"

# Process CSV data
rows = File.read_csv("sales.csv")
total = rows.map { |r| r.amount.to_f }.sum
puts "Total sales: #{total.to_currency}"

# Check before reading
if File.exist?("config.json")
  settings = File.read_json("config.json")
else
  warn "no config found, using defaults"
end

# Write results
File.write("output.txt", results.join("\n"))
File.append("log.txt", "#{Time.now}: task complete\n")
```

### Dir

```rush
Dir.files(".")                         # Files in current directory
Dir.files("src")                       # Files in src/
Dir.files(".", recursive: true)        # All files recursively
Dir.files("*.log")                     # Files matching pattern
Dir.files("*.rb", recursive: true)     # Pattern + recursive

Dir.dirs(".")                          # Subdirectories
Dir.exist?("path")                     # Check if directory exists
Dir.mkdir("path")                      # Create directory (+ parents)
```

**Examples:**

```rush
# Find all log files
logs = Dir.files("/var/log", recursive: true)
  .select { |f| f.Name =~ /\.log$/ }
puts "Found #{logs.count} log files"

# List project structure
Dir.dirs(".").each { |d| puts d.Name }

# Create output directory
Dir.mkdir("output/reports") unless Dir.exist?("output/reports")
```

### Time

```rush
Time.now                    # Current local DateTime
Time.utc_now                # Current UTC DateTime
Time.today                  # Today at midnight

# Duration arithmetic
yesterday = Time.now - 24.hours
next_week = Time.now + 7.days
timeout = 30.minutes + 45.seconds
```

---

## Built-in Commands

### Output

| Command | Description |
|---------|-------------|
| `puts "text"` | Print with newline |
| `print "text"` | Print without newline |
| `warn "text"` | Print to stderr |
| `die "error"` | Print error and exit |
| `printf "%s: %d\n" name count` | C-style formatted output |

`printf` supports: `%s` (string), `%d` (integer), `%f` (float), `%x` (hex), `%%` (literal %).

### Navigation

| Command | Description |
|---------|-------------|
| `cd path` | Change directory |
| `cd -` | Toggle to previous directory |
| `cd` | Go to home directory |
| `..` | Go up one directory (`cd ..`) |
| `...` | Go up two directories (`cd ../..`) |
| `....` | Go up three directories (`cd ../../..`) |
| `pwd` | Print working directory |
| `pushd dir` | Push current dir, cd to dir |
| `popd` | Pop directory from stack and cd |
| `dirs` | Show directory stack |
| `dirs -v` | Show stack with indices |

The dot shortcuts extend to any number of dots: each `.` beyond the first means one more level up.

`cd` also searches `CDPATH` — if a relative path isn't found in the current directory, it checks directories listed in `$CDPATH`.

### Environment

| Command | Description |
|---------|-------------|
| `export VAR=value` | Set environment variable |
| `unset VAR` | Remove environment variable |

### Aliases

| Command | Description |
|---------|-------------|
| `alias` | List all aliases |
| `alias name='cmd'` | Define alias (saved to config) |
| `unalias name` | Remove alias |

### History

| Command | Description |
|---------|-------------|
| `history` | Show last 50 entries |
| `history -c` | Clear all history |

### Input

| Command | Description |
|---------|-------------|
| `ask "prompt"` | Read line from user, return string |
| `ask "prompt", char: true` | Read single character |
| `read var` | Read line into variable |
| `read -p "prompt" var` | Read with prompt |

### Execution

| Command | Description |
|---------|-------------|
| `source file.rush` | Execute script in current session |
| `. file.rush` | Same as `source` |
| `exec cmd` | Replace shell with command |
| `sleep N` | Sleep N seconds |
| `exit [code]` | Exit Rush with optional code |
| `which cmd` | Find command location |

### Settings

| Command | Description |
|---------|-------------|
| `set vi` | Switch to vi edit mode |
| `set emacs` | Switch to emacs edit mode |
| `set -e` | Exit on error |
| `set +e` | Clear exit-on-error |
| `set -x` | Trace commands (print before executing) |
| `set +x` | Clear trace |
| `set -o pipefail` | Fail pipeline if any segment fails |
| `set +o pipefail` | Clear pipefail |
| `set --save key val` | Change setting and persist to config.json |
| `set --secret KEY val` | Save API key/token to secrets.rush |

### AI Assistant

The `ai` builtin lets you query an LLM directly from the shell. It streams responses in real-time and is fully context-aware (Rush language, OS, cwd, recent commands).

| Command | Description |
|---------|-------------|
| `ai "prompt"` | Ask a question |
| `ai "prompt" < file` | Ask with file context via redirection |
| `cat file \| ai "prompt"` | Pipe content as context |
| `failing-cmd \| ai "what happened?"` | Pipe errors — stderr is included automatically |
| `ai "prompt" > out.txt` | Capture response to file |
| `ai --provider ollama "prompt"` | Use a specific provider |
| `ai --model gpt-4o "prompt"` | Use a specific model |
| `ai --exec` | Execute the last AI response as a command |

**Fence stripping:** Markdown code fences (` ``` `) are automatically stripped from AI output. When the AI wraps commands in fences, only the clean content is displayed. `ai --exec` prefers code block content — so explanatory text is skipped and only the actual commands run.

**Setup:**

```rush
set --secret ANTHROPIC_API_KEY sk-ant-...    # Default provider
set --secret OPENAI_API_KEY sk-...           # For OpenAI
set --secret GEMINI_API_KEY AIza...          # For Google Gemini
# Ollama requires no key (runs locally)
```

**Config:**

```rush
set --save aiProvider anthropic    # anthropic, openai, gemini, ollama
set --save aiModel auto            # "auto" = provider default
```

**Custom providers:** Drop a JSON file in `~/.config/rush/ai-providers/`:

```json
{
  "name": "together",
  "endpoint": "https://api.together.xyz/v1/chat/completions",
  "format": "openai",
  "apiKeyEnvVar": "TOGETHER_API_KEY",
  "defaultModel": "meta-llama/Llama-3-70b-chat-hf"
}
```

### Hot Reload

| Command | Description |
|---------|-------------|
| `reload` | Reload config files (settings, theme, aliases) |
| `reload --hard` | Full binary restart preserving session state |

`reload --hard` serializes your session (variables, env, cwd, aliases, flags) to a temp file, restarts the Rush binary, and restores everything. Use this after updating the Rush binary with `install.sh`.

### Signals

| Command | Description |
|---------|-------------|
| `trap 'cmd' SIGNAL` | Register signal handler |
| `trap -l` | List registered traps |

```rush
trap 'echo interrupted' INT
trap 'cleanup' EXIT         # Run on shell exit
```

### Config Sync

| Command | Description |
|---------|-------------|
| `sync init github` | Initialize GitHub sync |
| `sync init ssh host:path` | Initialize SSH sync |
| `sync init path /dir` | Initialize path sync |
| `sync push` | Push config to remote |
| `sync pull` | Pull config from remote |
| `sync status` | Show sync status |

### Other

| Command | Description |
|---------|-------------|
| `reload` | Reload all config files |
| `clear` | Clear the screen |
| `help` | Show built-in help |

---

## The `ls` Builtin

Rush includes a custom `ls` implementation built on .NET (not PowerShell's `Get-ChildItem`). It provides Unix-style output with color coding and human-readable sizes.

### Flags

| Flag | Description |
|------|-------------|
| `-l` | Long format (permissions, owner, size, date, name) |
| `-a` / `-A` | Show hidden files (starting with `.`) |
| `-R` | Recursive — list subdirectories |
| `-r` | Reverse sort order |
| `-t` | Sort by modification time (newest first) |
| `-S` | Sort by file size (largest first) |
| `-1` | One entry per line |
| `-d` | List directory entry itself, not contents |
| `-F` | Append type indicator (`/` dir, `*` executable, `@` symlink) |
| `-G` | Omit group column in long format |
| `-h` | Human-readable sizes (always on) |

Flags can be combined: `ls -laR`, `ls -ltS`.

### Long Format Example

```
drwxr-xr-x  5 mark staff  160 Mar  1 14:34 src/
-rw-r--r--  1 mark staff 2.3K Mar  1 14:34 README.md
lrwxr-xr-x  1 mark staff   10 Mar  1 14:34 link -> target
-rwxr-xr-x  1 mark staff 8.1M Mar  1 14:34 rush*
```

### Color Coding

Files are colored by type: directories, executables, symlinks, archives (.zip, .tar.gz), images, config files (.json, .yaml), documents (.md, .txt), source code (.cs, .py, .js), and hidden files (dimmed).

### Size Display

Sizes are always human-readable: bytes, K, M, G.

---

## Command Translations

Rush translates common Unix commands to PowerShell equivalents. Native commands (git, docker, ssh, etc.) pass through directly.

### File Operations

| Unix | PowerShell | Notes |
|------|-----------|-------|
| `ls` | Custom builtin | See [ls builtin](#the-ls-builtin) |
| `cat file` | `Get-Content` | |
| `cp src dst` | `Copy-Item` | `-r` → `-Recurse` |
| `mv src dst` | `Move-Item` | |
| `rm file` | `Remove-Item` | `-r` → `-Recurse`, `-rf` → `-Recurse -Force` |
| `touch file` | `New-Item -ItemType File` | |
| `mkdir dir` | `New-Item -ItemType Directory` | |
| `find . -name "*.txt"` | `Get-ChildItem -Recurse -Filter` | |
| `head -5 file` | `Get-Content -TotalCount 5` | |
| `tail -5 file` | `Get-Content -Tail 5` | |

### Process & System

| Unix | PowerShell | Notes |
|------|-----------|-------|
| `ps` | `Get-Process` | |
| `kill PID` | `Stop-Process -Id` | |
| `env` | `Get-ChildItem Env:` | |
| `which cmd` | `Get-Command` | |
| `whoami` | `[Environment]::UserName` | |
| `hostname` | `[Environment]::MachineName` | |
| `df` | `Get-PSDrive -PSProvider FileSystem` | |
| `uptime` | Process start time calculation | |

### Text & Search

| Unix | PowerShell | Notes |
|------|-----------|-------|
| `grep pattern` | `Select-String -Pattern` | Piped: `Where-Object { $_ -match }` |
| `echo text` | `Write-Output` | |
| `sort` | `Sort-Object` | `-r` → `-Descending` |

### Pipeline Operations (After `|`)

These change behavior when used in a pipeline:

**Filtering:**

```rush
ps | where CPU > 10             # Filter by property
ps | where Name == "rush"
ps | where Name =~ /^dotnet/    # Regex match
ls | grep "\.txt$"              # Pattern filter
```

Where operators: `>`, `<`, `>=`, `<=`, `==`, `!=`, `=~` (match), `!~` (not match), `contains`.

**Selection:**

```rush
ps | select ProcessName, CPU    # Pick properties
ps | .ProcessName               # Dot-access shorthand
```

**Sorting:**

```rush
ps | sort CPU                   # Sort ascending
ps | sort -r CPU                # Sort descending
```

**Slicing:**

```rush
ls | first 5                    # First 5 items
ls | last 3                     # Last 3 items
ls | skip 10                    # Skip first 10
ls | head 5                     # Alias for first (default 10)
ls | tail 5                     # Alias for last (default 10)
```

**Aggregation:**

```rush
ls | count                      # Count items
ps | sum WorkingSet64           # Sum a property
ps | avg CPU                    # Average
ps | min Id                     # Minimum
ps | max WorkingSet64           # Maximum
```

**Uniqueness:**

```rush
ls | distinct                   # Unique values
ls | uniq                       # Alias for distinct
ls | distinct Name              # Unique by property
```

**Format conversion:**

```rush
ps | as json                    # → JSON
ps | as csv                     # → CSV
ps | as table                   # → Formatted table
ps | as list                    # → Formatted list
cat data.json | from json       # Parse JSON
cat data.csv | from csv         # Parse CSV
json config.json                # Read + parse JSON file
```

**I/O:**

```rush
ls | tee files.txt | count      # Save and pass through
ls | tee -a log.txt             # Append mode
```

---

## Built-in Variables

| Variable | Description |
|----------|-------------|
| `$?` | Last command exit status (object) |
| `$?.ok?` | True if last command succeeded |
| `$?.failed?` | True if last command failed |
| `$?.code` | Numeric exit code |
| `ARGV` | Script arguments (array) |
| `__FILE__` | Current script file path |
| `__DIR__` | Current script directory |
| `env.HOME` | Environment variable (dot access) |
| `env["PATH"]` | Environment variable (bracket access) |
| `os` | Operating system name |
| `hostname` | Machine name |
| `rush_version` | Rush version string |
| `is_login_shell` | True if launched as login shell |

---

## Tips & Tricks

### PowerShell Passthrough

Any PowerShell cmdlet works directly in Rush:

```rush
Get-Process | Where-Object CPU -gt 50
[Math]::PI                      # → 3.14159265358979
[DateTime]::Now.DayOfWeek       # → Monday
[System.IO.Path]::GetExtension("file.txt")  # → .txt
```

### Debugging

```rush
set -x                          # Trace — prints each command before running
set -e                          # Stop on first error
set -o pipefail                 # Pipeline fails if any segment fails
```

### Quick Navigation

```rush
cd -                            # Toggle between last two directories
..                              # Up one level (cd ..)
...                             # Up two levels (cd ../..)
....                            # Up three levels (cd ../../..)
pushd /tmp                      # Save current dir, go to /tmp
popd                            # Return to saved dir
```

### Mixing Rush and Shell

Rush scripting and shell commands coexist naturally:

```rush
# Rush variable + shell command
pattern = "error"
grep #{pattern} /var/log/syslog | tail 20

# Capture shell output into Rush variable
branch = $(git branch --show-current).strip
count = $(ls | wc -l).strip.to_i

# Use Rush control flow around shell commands
if File.exist?("Makefile")
  make clean && make
else
  warn "no Makefile found"
end
```

### Size Literals

PowerShell size constants work in Rush:

```rush
1kb                             # 1,024
5mb                             # 5,242,880
2gb                             # 2,147,483,648
```

### History Tricks

```rush
!!                              # Repeat last command
!$                              # Last argument of previous command
!42                             # Run command #42 from history
!git                            # Run most recent command starting with "git"
```
