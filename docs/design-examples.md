# Rush Language Design — Conversion Examples

These real-world conversions from PowerShell and bash define the target syntax.
Resolved decisions are marked with **[RESOLVED]**. Open questions with **[DECISION]**.

---

## Example 1: check-manifest.rush (from bash)

### Original: bash (74 lines)
See `rcforge/tools/check-manifest.sh`

### Rush conversion:

```rush
#!/usr/bin/env rush
# check-manifest.rush — Manifest verification tool
# Checks filesystem against manifest and vice versa.

project_dir = "#{env.HOME}/src/rcforge"              # [RESOLVED] env.HOME for env vars
manifest_file = "#{project_dir}/file-manifest.txt"
ignore_file = "#{project_dir}/tools/.manifest_ignore"

# Parse manifest: skip to FILES: marker, drop comments, take first column
# [RESOLVED] File.read_lines / .skip_while / .reject / .map — method chaining
manifest = File.read_lines(manifest_file)
  .skip_while { |l| !l.include?("FILES:") }        # [RESOLVED] ? in method names (predicates)
  .skip(1)
  .reject { |l| l.empty? || l.start_with?("#") }
  .map { |l| l.split.first }

ignore = File.read_lines(ignore_file)

# [RESOLVED] Dir.list() for listing — replaces `find . -type f`
files = Dir.list(".", type: "file", recursive: true)
  .reject { |f| f.start_with?(".") }
  .sort

added = 0; ignored_count = 0; skipped = 0; not_found = 0

for fname in files
  next if manifest.include?(fname)                  # [RESOLVED] next for skip
  next if ignore.include?(fname)

  # [RESOLVED] ask() built-in for interactive prompts
  ans = ask "[MISSING] #{fname} — (m)anifest (i)gnore (s)kip? ", char: true

  # [RESOLVED] match/when/end (not case/esac, not switch/case)
  match ans
  when "m"
    puts " add to manifest"
    File.append manifest_file, "# #{fname.ljust(47)} #{fname}\n"
    added += 1
  when "i"
    puts " add to ignore"
    File.append ignore_file, "#{fname}\n"
    ignored_count += 1
  else
    puts " skip"
    skipped += 1
  end
end

puts ""

# Reverse check — manifest entries missing from disk
for fname in manifest
  unless files.include?(fname)
    puts "[NOT FOUND] #{fname}"
    not_found += 1
  end
end

puts ""
puts "#{added} added to manifest"
puts "#{ignored_count} added to ignore"
puts "#{skipped} skipped"
puts "#{not_found} not found on disk"
```

### Design decisions from this conversion:

| # | Decision | Status | Alternatives considered | Notes |
|---|----------|--------|----------------------|-------|
| 1 | `env.HOME` for env vars | ✅ Resolved | `ENV["HOME"]`, `$HOME` | Dot access for clean names, bracket `env["X-Y"]` for edge cases. Transpiles to `$env:HOME` |
| 2 | `File.read_lines()` / `File.append()` | ✅ Resolved | `cat`, `>>`, shell commands | Clean OO file ops. Shell commands still work for one-offs |
| 3 | `?` in method names | ✅ Resolved | Drop the `?`, use `is_` prefix | `.empty?`, `.include?`, `.start_with?` — self-documenting predicates, clearly boolean |
| 4 | `match/when/end` | ✅ Resolved | `case/when/end`, `switch/case` | `match` is modern. Avoids bash `case/esac` baggage |
| 5 | `next` for skip | ✅ Resolved | `continue` | Clean, short, reads naturally in blocks |
| 6 | `ask()` built-in | ✅ Resolved | `input()`, `read`, `prompt()` | `ask` is human-readable. `char: true` for single-char input |
| 7 | `Dir.list()` | ✅ Resolved | `Dir.glob()`, `Dir.files()`, `find` | One method with named arg filters (`type:`, `recursive:`, `hidden:`). Shell `find` still works for complex cases |
| 8 | `+=` for increment (no `++`) | ✅ Resolved | `++`, `+= 1` | `+=` is accessible to non-C developers. No pre/post ambiguity |

---

## Example 2: test-domain-join.rush (from PowerShell)

### Original: PowerShell (131 lines)
See `COI/Planning/migration-notes/scripts/Test-DomainJoinReady.ps1`

### Rush conversion:

```rush
#!/usr/bin/env rush
# test-domain-join.rush — AD domain join readiness checker

# [RESOLVED] Colored strings via method: "text".green, "text".red
# Transpiles to: Write-Host "text" -ForegroundColor Green
puts ""
puts "========================================".cyan
puts "  Domain Join Readiness Test".cyan
puts "========================================".cyan
puts ""

dc = "192.168.111.52"
dc_name = "COR1S02"
domain = "ContinentalOptical.local"
errors = 0

# === 1. Basic Network ===
puts "[1/6] Basic Network...".yellow

# [RESOLVED] PowerShell cmdlets pass through with translated chaining
ip = Get-NetIPAddress(-AddressFamily: "IPv4")
  .select { |a| a.IPAddress =~ /^10\.10\.10\./ }
  .first

if ip
  puts "  [OK] IP: #{ip.IPAddress} (VLAN 10)".green
else
  puts "  [WARN] Not on 10.10.10.x subnet".yellow
  ip = Get-NetIPAddress(-AddressFamily: "IPv4")
    .select { |a| a.PrefixOrigin == "Dhcp" }
    .first
  puts "  Current IP: #{ip&.IPAddress}".gray       # [RESOLVED] &. safe navigation
end

gateway = Get-NetRoute(-DestinationPrefix: "0.0.0.0/0").first.NextHop
puts "  Gateway: #{gateway}".gray

# === 2. DC Ping ===
puts "[2/6] DC Ping...".yellow

if ping(dc, count: 2, quiet: true)                  # [RESOLVED] named args for Rush functions
  puts "  [OK] Can ping #{dc}".green
else
  puts "  [FAIL] Cannot ping #{dc}".red
  errors += 1
end

# === 3. DNS Resolution ===
puts "[3/6] DNS Resolution...".yellow

# [RESOLVED] begin/rescue for error handling
begin
  resolved = Resolve-DnsName(dc_name)
  puts "  [OK] #{dc_name} → #{resolved.IPAddress}".green
rescue
  puts "  [FAIL] Cannot resolve #{dc_name}".red
  errors += 1
end

begin
  resolved = Resolve-DnsName(domain)
  puts "  [OK] #{domain} resolves".green
rescue
  puts "  [FAIL] Cannot resolve #{domain}".red
  errors += 1
end

# === 4. Port Connectivity ===
puts "[4/6] Port Connectivity...".yellow

# [RESOLVED] Array of hashes
ports = [
  { port: 53,  name: "DNS" },
  { port: 88,  name: "Kerberos" },
  { port: 135, name: "RPC" },
  { port: 389, name: "LDAP" },
  { port: 445, name: "SMB" },
  { port: 636, name: "LDAPS" },
]

for p in ports
  # [RESOLVED] Shell commands and PS cmdlets intermix freely
  result = Test-NetConnection(dc, -Port: p.port, -WarningAction: "SilentlyContinue")
  if result.TcpTestSucceeded
    puts "  [OK] Port #{p.port} (#{p.name})".green
  else
    puts "  [FAIL] Port #{p.port} (#{p.name})".red
    errors += 1
  end
end

# === 5. DC Locator ===
puts "[5/6] DC Locator...".yellow

nltest_out = $(nltest /dsgetdc:#{domain} 2>&1)      # [RESOLVED] $() for command capture
if $?.ok?                                             # [RESOLVED] $?.ok? / $?.failed?
  puts "  [OK] Found DC for #{domain}".green
  nltest_out.lines
    .select { |l| l =~ /DC:/ }
    .each { |l| puts "  #{l}".gray }
else
  puts "  [FAIL] Cannot locate DC".red
  errors += 1
end

# === 6. Share Access ===
puts "[6/6] SYSVOL/NETLOGON Access...".yellow

for share in ["SYSVOL", "NETLOGON"]
  path = "\\\\#{dc_name}\\#{share}"
  if File.exist?(path)
    puts "  [OK] #{path}".green
  else
    puts "  [FAIL] #{path}".red
    errors += 1
  end
end

# === Summary ===
puts ""
puts "========================================".cyan
if errors == 0
  puts "  READY TO JOIN DOMAIN".green
  puts "========================================".cyan
  puts ""
  puts "Run: Add-Computer -DomainName #{domain} -Credential (Get-Credential)".yellow
else
  puts "  #{errors} ERROR(S) — FIX BEFORE JOINING".red
  puts "========================================".cyan
  puts ""
  puts "Common fixes:".yellow
  puts "  - Check DNS points to #{dc}".gray
  puts "  - Run Check-ServerFirewalls.ps1 -Fix on DC".gray
  puts "  - Verify UDM-SE allows VLAN 10 → VLAN 111".gray
end
```

### Design decisions from this conversion:

| # | Decision | Status | Alternatives considered | Notes |
|---|----------|--------|----------------------|-------|
| 1 | `"text".green` / `"text".red` | ✅ Resolved | `color(:green, "text")`, ANSI codes | Colors are SO common in shell scripts they should be ergonomic. Extends to numeric formatting too |
| 2 | PS cmdlets pass through | ✅ Resolved | Alias everything to snake_case | Standing on the shoulders of giants. Rush doesn't hide PS, it makes it pleasant |
| 3 | `&.` safe navigation | ✅ Resolved | `?.` (C#/JS style), no safe nav | `ip&.IPAddress` — nil returns nil instead of crashing. Very useful in shell scripts |
| 4 | `$()` command capture | ✅ Resolved | backticks, `exec()` | Bash convention, universally understood |
| 5 | `$?.ok?` / `$?.failed?` | ✅ Resolved | `$?.success`, `$? == 0` | Predicate methods on exit status — self-documenting |
| 6 | Hash literals `{ key: val }` | ✅ Resolved | PowerShell `@{ Key = Val }` | Cleaner syntax. Transpiles to PS hashtables |
| 7 | Array literals `[1, 2, 3]` | ✅ Resolved | PowerShell `@(1, 2, 3)` | Standard square brackets. Transpiles to PS arrays |
| 8 | Named args `count: 2` | ✅ Resolved | `-Count 2` (PS style) | Colon syntax for Rush functions. PS flags still work for PS cmdlets |

---

## Resolved Design Questions

All core design questions from Examples 1 and 2 are now resolved.

| # | Question | Resolution |
|---|----------|-----------|
| 1 | PS cmdlet syntax | **Pass through.** Standing on shoulders of giants. Rush doesn't hide PS, it makes it pleasant |
| 2 | Method chaining on cmdlets | **Yes.** `Get-NetIPAddress().select { }` — PS power + clean ergonomics. The core value prop |
| 3 | `"text".green` colored output | **Yes.** Colors are too common in shell scripts NOT to be ergonomic. Extends to numeric formatting |
| 4 | `?` predicate methods | **Yes.** So obvious you don't need prior exposure. Self-documenting boolean methods |
| 5 | `next` vs `continue` | **Both.** `next` preferred — "continue sounds like run the next line." `continue` as alias for familiarity |

---

## Example 3: deploy-service.rush (function-focused)

Demonstrates functions, return values, named args, and method chaining on numbers/strings.

```rush
#!/usr/bin/env rush
# deploy-service.rush — Deploy with health checks and rollback

def health_check(host, port: 443, timeout: 5)
  begin
    result = $(curl -s -o /dev/null -w "%{http_code}" --max-time #{timeout} "https://#{host}:#{port}/health")
    code = result.strip.to_i
    if code >= 200 && code < 300
      puts "  [OK] #{host} responded #{code}".green
      true
    else
      puts "  [FAIL] #{host} responded #{code}".red
      false
    end
  rescue => e
    puts "  [FAIL] #{host}: #{e.message}".red
    false
  end
end

def deploy(target, version, dry_run: false)
  puts ""
  puts "=== Deploying v#{version} to #{target} ===".cyan

  if dry_run
    puts "  (dry run — no changes)".yellow
    return true
  end

  # Shell commands just work inside functions
  git tag -a "v#{version}" -m "Deploy to #{target}"
  docker build -t "myapp:#{version}" .

  if $?.failed?
    puts "  Docker build failed!".red
    return false
  end

  docker push "registry.example.com/myapp:#{version}"
  ssh "#{target}.example.com" "docker pull registry.example.com/myapp:#{version} && docker-compose up -d"

  # Health check with retry
  3.times { |attempt|
    puts "  Health check attempt #{attempt + 1}...".gray
    return true if health_check(target)
    sleep 2
  }

  # All retries failed — rollback
  puts "  Health checks failed — rolling back!".red
  ssh "#{target}.example.com" "docker-compose rollback"
  false
end

# --- Main ---
target = ARGV[0] || die("usage: deploy.rush <target> [version]")
version = ARGV[1] || $(git describe --tags --abbrev=0).strip

# [RESOLVED] env.HOME, env[/regex/] for filtered env vars
puts "User: #{env.USER}".gray
puts "Docker config: #{env.HOME}/.docker/config.json".gray

# Show all deploy-related env vars
puts "Deploy env vars:".gray
env[/DEPLOY/].each { |k, v| puts "  #{k}=#{v}".gray }

success = deploy(target, version)

if success
  puts ""
  puts "Deploy complete!".green
  elapsed = Time.now - start_time
  puts "Took #{elapsed.round(1)} seconds".gray
else
  puts ""
  puts "Deploy FAILED.".red
  exit 1
end
```

### Design decisions from this conversion:

| # | Decision | Status | Notes |
|---|----------|--------|-------|
| 1 | Functions with `def/end` | ✅ Resolved | Positional + named args. Implicit return. Shell commands work inside |
| 2 | `env[/DEPLOY/]` regex filter | ✅ New | Filter env vars by regex. Returns hash of matching key/value pairs |
| 3 | Numeric methods `.round(1)` | ✅ New | Method chaining on numbers. `.to_i`, `.to_f`, `.round`, `.abs` |
| 4 | `3.times { \|i\| }` | ✅ Resolved | Numeric iteration. Already in spec |
| 5 | Implicit return | ✅ Resolved | Last expression is return value. Explicit `return` also works |

---

## Emerging Design: Formatting Methods

The `"text".green` pattern extends to a broader idea: **method chaining on everything**.

### String formatting
```rush
"hello".green                    # colored output
"hello".upcase                   # "HELLO"
"hello world".split              # ["hello", "world"]
"  padded  ".strip               # "padded"
```

### Numeric formatting
```rush
3.1415.round(2)                  # 3.14
14.25.to_currency                # "$14.25"
4.25.to_currency(pad: 10)       # "     $4.25"   (right-aligned in field)
1048576.to_filesize              # "1.0 MB"
0.8734.to_percent                # "87.3%"
```

### Daisy chaining
```rush
price.round(2).to_currency.green                     # "$14.25" in green
elapsed.round(1).to_s + " seconds"                   # "3.2 seconds"
(bytes / 1mb).round(1).to_s.rjust(8) + " MB"        # "    14.2 MB"
```

### Regex — clean and obvious
```rush
# Match operator: =~
name =~ /^test/                  # true if name starts with "test"
name !~ /admin/                  # true if name does NOT contain "admin"

# Regex literals
pattern = /error|warn|fatal/i    # case-insensitive

# String methods for substitution (no sed syntax)
line.sub(/^#\s*/, "")           # remove leading comment — first match
text.gsub(/\t/, "  ")           # replace all tabs with spaces
path.scan(/[^\/]+/)             # extract all path components

# Env var filtering
env[/PATH/]                      # all env vars with "PATH" in the name
env[/^RUSH_/]                    # all env vars starting with "RUSH_"
```

**Regex design principles:**
- `/pattern/` for literals — universally understood
- `=~` for match, `!~` for not match — unambiguous, widely recognized
- `.sub` / `.gsub` / `.scan` for substitution — method syntax, not `s///` syntax
- No obscure operators to memorize
