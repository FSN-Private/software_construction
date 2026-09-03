# Quickstart: Validate Project-Scoped Ponytail Skills

Run from the repository root after implementation.

## Structural and integrity validation

```powershell
$Expected = @('ponytail','ponytail-audit','ponytail-debt','ponytail-gain','ponytail-help','ponytail-review')
$Expected | ForEach-Object {
  $Path = ".agents/skills/$_/SKILL.md"
  if (-not (Test-Path -LiteralPath $Path)) { throw "Missing $Path" }
  $Declared = (Select-String -LiteralPath $Path -Pattern '^name: ?["'']?([^"'']+)["'']?$').Matches.Groups[1].Value
  if ($Declared -ne $_) { throw "$Path declares '$Declared'" }
  "$_ $((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant())"
}
```

Expected: six lines, one for each skill; each hash matches `third-party.md`.

## Scope validation

```powershell
git diff --check master...HEAD
git diff --name-only master...HEAD
```

Expected: no whitespace errors. Changed paths are limited to the six `.agents/skills/ponytail*/SKILL.md` files and `docs/features/013-ponytail-project-skill/`; no hook, package, lockfile, application, or global-configuration path appears.

## Activate

Start a new Codex thread rooted in this repository, then invoke `ponytail-help` or request a coding task that matches the `ponytail` skill description. No install command or Node.js runtime is required.
