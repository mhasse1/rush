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

### Clean, Intent-Driven Syntax

| Bash | Rush |
|------|------|
| `name="world"` | `name = "world"` |
| `echo "Hello $name"` | `puts "Hello #{name}"` |
| `if [ -f f ]; then ... fi` | `if File.exist?("f") ... end` |
| `for x in a b c; do ... done` | `for x in ["a", "b", "c"] ... end` |
| `for f in *; do echo $f; done` | `for f in Dir.list("."); puts f; end` |
| `arr=(1 2 3); echo ${arr[0]}` | `arr = [1, 2, 3]; puts arr[0]` |
| `echo $((5 + 3))` | `puts 5 + 3` |

### Pipeline Operators

Filter, slice, and aggregate structured data without `awk`/`sed`/`jq`:

```bash
# Filter by property (known commands auto-objectify when piped)
ps -ef | where CMD =~ rush
netstat -an | where State == LISTEN

# Select columns
ps -ef | select PID, CMD

# Count
ls | count

# Slice
ls | first 5                    ls | last 3
ls | skip 2                     ls | skip 2 | first 3

# Aggregate (on structured data via from json or objectify)
cat data.json | from json | sum amount
cat data.json | from json | avg score
cat data.json | from json | min score
cat data.json | from json | max score

# Unique (works unsorted)
data | distinct                  data | distinct Name

# Sort in pipeline
data | sort Name                 data | sort -r

# Extract single property
cat data.json | from json | .name

# Format & parse
cat data.json | from json        cat data.csv | from csv
data | as json                   data | as csv

# Save and pass through
ls | tee listing.txt | count
```

### objectify — Text to Objects

Known commands (ps, netstat, df, docker ps, kubectl get, etc.) **auto-objectify** when piped
to pipeline operators — no need to type `| objectify |` explicitly:

```bash
ps -ef | where CMD =~ rush | select PID, CMD
netstat -an | where State == LISTEN | select LocalAddress | count
docker ps | where Status =~ Up | select Names, Ports
```

For other commands, use `objectify` explicitly:

```bash
lsblk | objectify | where TYPE == disk
```

Customize which commands auto-objectify in `~/.config/rush/objectify.rush`.

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
Dir.list(".", recursive: true)  Dir.mkdir("path/to/dir")
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

### Loops

```bash
# Bash                              # Rush
for f in *; do                       for f in Dir.list(".")
  echo "$f"                            puts f
done                                 end

for x in 1 2 3; do                   for x in [1, 2, 3]
  echo "$x"                            puts x
done                                 end
```

### Platform Blocks

```bash
macos                               linux
  brew install ripgrep                apt install ripgrep
end                                 end
```

---

## Side by Side

| Task | Bash | Rush |
|------|------|------|
| Filter processes | `ps aux \| grep chrome` | `ps aux \| where COMMAND =~ chrome` |
| Count files | `ls | wc -l` | `ls | count` |
| Extract column | `awk '{print $1}'` | `| .PropertyName` |
| Top 5 items | `head -5` | `| first 5` |
| Unique lines | `sort | uniq` | `| distinct` |
| Parse JSON | `jq '.name'` | `File.read_json("f").name` |
| Check file exists | `[ -f file ]` | `File.exist?("file")` |
| String interpolation | `"Hello $name"` | `"Hello #{name}"` |
| If statement | `if [ $x -gt 5 ]; then ... fi` | `if x > 5 ... end` |
| For loop | `for x in 1 2 3; do ... done` | `for x in [1,2,3] ... end` |
| Loop over files | `for f in *; do echo $f; done` | `for f in Dir.list(".") ... end` |
| Sum a column | `awk '{s+=$1}END{print s}'` | `| sum ColumnName` |
| Export as CSV | (manual) | `| as csv` |
| Export as JSON | (manual) | `| as json` |

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

### Colors & Theming

Rush detects your terminal background and configures native command colors:
- **Dark terminal** → bold/bright colors for `ls`, `grep`, etc.
- **Light terminal** → non-bold/darker colors that stay readable

No setup needed. Override with `set theme dark` or `set theme light`.
Disable all colors with `NO_COLOR=1`. Rush respects existing `LS_COLORS`/`GREP_COLORS` if set.

```bash
set bg "#222733"            # Tell Rush your exact background color
setbg "#222733"             # Shorthand
set bg off                  # Disable (default)
```

### PATH Management

```bash
path                            # Show PATH with existence check
path add /opt/bin               # Add (session only)
path add --save /opt/bin        # Add and persist
path add --front --save /opt/bin # Prepend and persist
path edit --save                # Edit in $EDITOR
path check                  # Show PATH with existence + duplicate checks
path dedupe                 # Remove duplicate entries
path add                    # Multi-line block:
  /opt/homebrew/bin         #   one directory per line
  ~/.local/bin              #   terminated by 'end'
end
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
