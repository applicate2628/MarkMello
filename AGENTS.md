# MarkMello Agent Rules

These repo-local rules supplement the user's global rules. Keep them short, practical, and specific to this repository.

## Commit Messages

Commit messages should follow the rules defined in:

- `.agents/GitCommitMessages.md` — commit message format and rules.
- `.agents/GitCommitEmoji.md` — emoji-to-change-type mapping.

Short format:

`:emoji: type(scope)!: short description`

Example:

`:sparkles: feat(api)!: send an email to the customer when a product is shipped`

## Claude and External Provider Reviews

Use file-based prompts for Claude or any other external provider CLI that receives a substantive task.

Rules:

- Put the full task prompt under `.scratch/claude-prompts/` or another ignored scratch folder.
- Feed the prompt through stdin or a provider-supported file input. Do not pass the review/task body as a command-line argument.
- Save stdout and stderr to explicit files next to the prompt file.
- Use read-only Claude tool sets for review unless implementation is explicitly requested.
- For self-contained reviews, prefer no repo tools: `--tools "" --no-session-persistence --strict-mcp-config --mcp-config '{"mcpServers":{}}'`.
- For repo-reading reviews, allow only the read-only tools needed, for example `--tools "Read,Grep,Glob"`.
- For `opus` / `max` / deep-review runs, do not assume 30 minutes is enough. Use a 45-60 minute foreground timeout or a background process with redirected output.
- If the foreground call times out, treat it as a checkpoint. Check the exact Claude PID, CPU time, stdout/stderr files, and whether output is still being produced before stopping or retrying.
- Do not launch a duplicate Claude run while a prior one is still active and observable.
- If a Claude process becomes orphaned or unobservable, record the PID, command line, timeout, output paths, and reason before stopping it.

Example self-contained review:

```powershell
$prompt = ".scratch\claude-prompts\review.md"
$stdout = ".scratch\claude-prompts\review.out.txt"
$stderr = ".scratch\claude-prompts\review.err.txt"
Get-Content -Raw $prompt |
  claude -p --model opus --effort max --permission-mode plan `
    --tools "" --no-session-persistence --strict-mcp-config `
    --mcp-config '{"mcpServers":{}}' --output-format text `
    1> $stdout 2> $stderr
```

Example repo-reading review:

```powershell
Get-Content -Raw $prompt |
  claude -p --model opus --effort max --permission-mode plan `
    --tools "Read,Grep,Glob" --output-format text `
    1> $stdout 2> $stderr
```

## Verification Before Closeout

Before claiming MarkMello UI or renderer work is complete, run the narrowest relevant checks first and then the broader checks when the change is ready:

- `dotnet build MarkMello.sln --no-restore`
- `dotnet test MarkMello.sln --no-restore --no-build -m:1 -- xunit.parallelizeTestCollections=false`
- `npm --prefix src\MarkMello.Applicate.Desktop run check:renderer`
- `npm --prefix src\MarkMello.Applicate.Desktop run build:renderer`
- `git diff --check`

For visual renderer changes, also perform or request a manual visual pass. Build success is not visual correctness.

## Terms and Abbreviations

- CLI: Command-Line Interface; a tool invoked from a shell.
- CPU: Central Processing Unit; useful for checking whether a long provider process is still active.
- PID: Process Identifier; numeric operating-system identifier for an exact process.
- Provider: an external model/runtime such as Claude.
- Stderr: standard error stream for diagnostics.
- Stdin: standard input stream used to feed prompt-file contents.
- Stdout: standard output stream for provider results.
- UI: User Interface.
