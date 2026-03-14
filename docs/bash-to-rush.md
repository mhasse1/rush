# Moving from Bash/Zsh to Rush

## What Stays the Same

**Everything you know works.** Native commands, pipes, redirects, env vars, history — all unchanged.

```bash
ls -la                         grep -i pattern file.txt
find . -name "*.txt"           git status && git push
cmd > out.txt                  cmd >> log.txt
cmd1 && cmd2 || echo fail      cmd &
cd ~/projects                  $HOME, $PATH
!!  !$  !42  Ctrl+R            pushd  popd  dirs
```

---

## What's New

### Ruby-Like Syntax

| Bash | Rush |
|------|------|
| `name="world"` | `name = "world"` |
| `echo "Hello $name"` | `puts "Hello #{name}"` |
| `if [ -f f ]; then ... fi` | `if File.exist?("f") ... end` |
| `for x in a b c; do ... done` | `for x in [a, b, c] ... end` |
| `arr=(1 2 3); echo ${arr[0]}` | `arr = [1, 2, 3]; puts arr[0]` |
| `echo $((5 + 3))` | `puts 5 + 3` |

### Pipeline Operators

Filter, slice, and aggregate structured data without `awk`/`sed`/`jq`:

```bash
# Filter by property
ps | where CPU > 10
netstat | objectify | where State == LISTEN

# Select columns
ps | select ProcessName, CPU

# Count
ls | count

# Slice
ls | first 5                    ls | last 3
ls | skip 2                     ls | skip 2 | first 3

# Aggregate
ps | sum WorkingSet64            ps | avg CPU
ps | min CPU                     ps | max CPU

# Unique (works unsorted)
data | distinct                  data | distinct Name

# Sort in pipeline
data | sort Name                 data | sort -r

# Extract single property
ps | .ProcessName                data | .items[].id

# Format & parse
ps | as json                     cat data.json | from json
ps | as csv                      cat data.csv | from csv
ps | as table                    ps | as list

# Save and pass through
ps | tee processes.txt | count
```

### objectify — Text to Objects

Convert any tabular command output to structured objects:

```bash
netstat -an | objectify | where State == LISTEN | select LocalAddress | count
df -h | objectify | where Use% > 80
docker ps | objectify | where Status =~ /Up/ | select Names, Ports
```

### String Methods

```bash
"  hello  ".strip                "hello".upcase
"hello world".split(" ")        "hello".include?("ell")
"hello".start_with?("hel")     "hello".replace("l", "L")
"error".red                     "ok".green          # colored output
```

### File & Dir Stdlib

```bash
File.read("config.txt")         File.read_json("data.json")
File.write("out.txt", data)     File.append("log.txt", line)
File.exist?("path")             File.size("path")
Dir.files(".", recursive: true) Dir.mkdir("path/to/dir")
```

### Duration Literals

```bash
sleep 2.seconds
timeout = 5.minutes
elapsed = 1.hours + 30.minutes
```

### AI Command

```bash
ai "explain this error"
cat log.txt | ai "what went wrong?"
docker ps | ai "anything unhealthy?"
```

### SQL Command

```bash
sql add @db "sqlite:///data.db"
sql @db "SELECT * FROM users WHERE age > 18"
sql @db "SELECT * FROM logs" | objectify | where status == error
```

### Platform Blocks

```bash
macos { brew install ripgrep }
linux { apt install ripgrep }
win64 { choco install ripgrep }
```

---

## Side by Side

| Task | Bash | Rush |
|------|------|------|
| Filter processes | `ps aux \| grep chrome` | `ps \| where ProcessName == chrome` |
| Count files | `ls \| wc -l` | `ls \| count` |
| Extract column | `awk '{print $1}'` | `\| .PropertyName` |
| Top 5 items | `head -5` | `\| first 5` |
| Unique lines | `sort \| uniq` | `\| distinct` |
| Parse JSON | `jq '.name'` | `File.read_json("f").name` |
| Check file exists | `[ -f file ]` | `File.exist?("file")` |
| String interpolation | `"Hello $name"` | `"Hello #{name}"` |
| If statement | `if [ $x -gt 5 ]; then ... fi` | `if x > 5 ... end` |
| For loop | `for x in 1 2 3; do ... done` | `for x in [1,2,3] ... end` |
| Format as table | `column -t` | `\| as table` |
| Sum a column | `awk '{s+=$1}END{print s}'` | `\| sum ColumnName` |

---

## Configuration

```
~/.config/rush/config.json     # Settings (edit mode, theme, aliases, AI provider)
~/.config/rush/init.rush       # Startup script (aliases, exports, functions)
~/.config/rush/secrets.rush    # API keys (never synced)
```

Key settings:
```bash
set vi                          # Vi mode (default)
set emacs                       # Emacs mode
set --save aiProvider anthropic # AI provider
set --secret API_KEY "value"    # Save secret
```

### PATH Management

```bash
path                            # Show PATH with existence check
path add /opt/bin               # Add (session only)
path add --save /opt/bin        # Add and persist
path add --front --save /opt/bin # Prepend and persist
path edit --save                # Edit in $EDITOR
```

---

## Keybindings (Vi Mode)

```
Esc         Normal mode          i/a/I/A     Insert mode
w/b/e       Word motions         0/$         Line start/end
f{c}/F{c}   Find char            /pattern    Search history
x/D/C       Delete/change        u           Undo
3w          Count + motion       .           Repeat last change
```

---

## Gotchas

**No `$` on variables** — `name = "world"`, not `$name = "world"`

**`#{}` not `${}` for interpolation** — `"Hello #{name}"`, not `"Hello ${name}"`

**`end` not `fi`/`done`** — blocks close with `end`, not `fi`, `done`, or `}`

**`puts` not `echo` for rush expressions** — `echo` still works for simple strings, but `puts` handles rush expressions and interpolation

**Semicolons work as newlines** — `if x > 5; puts "yes"; end` on one line
