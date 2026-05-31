# Инструкция для новой агентской сессии

Эта памятка нужна, чтобы новая сессия не восстанавливала контекст MarkMello с нуля. Сначала прочитай `AGENTS.md`, затем этот файл, затем проверяй факты в текущем checkout.

## Быстрый старт

1. Проверь, где стоишь:

```powershell
git status --short -uall
git branch --show-current
git log --oneline --decorate -12
```

Если рабочее дерево грязное, считай эти изменения пользовательскими или уже начатой работой. Не откатывай их без прямого разрешения. Перед правкой файла с локальными изменениями сначала прочитай `git diff -- <path>`.

2. Перед багфиксом собери наблюдаемые данные:

```powershell
git diff -- src tests
rg -n "problem-pattern" src tests
```

Не начинай патчить UI, renderer, tab switching, TOC, minimap, theme sync или `Ctrl+E` без воспроизведения, лога, кадра, видео или конкретной `file:line`-гипотезы.

3. Если уже есть хороший коммит, сравнивай с ним через Git, а не изобретай новый pipeline:

```powershell
git log --oneline --decorate -30
git show --stat <commit>
git diff <known-good>..HEAD -- src tests
```

Для рискованного сравнения лучше использовать отдельный worktree, а не ломать текущий checkout.

## Главные зоны проекта

| Зона | Где смотреть |
| --- | --- |
| Applicate desktop shell | `src/MarkMello.Applicate.Desktop/` |
| Renderer TypeScript source | `src/MarkMello.Applicate.Desktop/RendererWeb/src/` |
| Bundled renderer output | `src/MarkMello.Applicate.Desktop/RendererWeb/assets/renderer.js` |
| WebView host and transactions | `src/MarkMello.Applicate.Desktop/Rendering/` |
| Native/WebView bridge | `src/MarkMello.Applicate.Desktop/ApplicateSiblingMountBridge.cs` |
| Web markdown view | `src/MarkMello.Applicate.Desktop/Views/ApplicateWebMarkdownDocumentView.cs` |
| Main window and hotkeys | `src/MarkMello.Applicate.Desktop/ApplicateMainWindow.cs` |
| Tabs, reading/edit state, TOC VM | `src/MarkMello.Presentation/ViewModels/MainWindowViewModel*.cs` |
| Applicate tests | `tests/MarkMello.Applicate.Tests/` |
| Presentation tests | `tests/MarkMello.Presentation.Tests/` |
| Renderer Vitest tests | `src/MarkMello.Applicate.Desktop/RendererWeb/vitest/` |

`renderer.ts` is the source of truth for the WebView script. If it changes, rebuild `RendererWeb/assets/renderer.js` and include the generated bundle intentionally.

## Запуск приложения

Собери проект:

```powershell
dotnet build MarkMello.sln --no-restore
```

Запусти Applicate на конкретном документе:

```powershell
Start-Process `
  -FilePath .\src\MarkMello.Applicate.Desktop\bin\Debug\net10.0\MarkMello.Applicate.exe `
  -ArgumentList .\sample.md
```

Для диагностики переключений используй тяжелый документ в `.scratch/`, а не committed sample:

```powershell
$dir = ".scratch\mode-toggle-diagnostics"
New-Item -ItemType Directory -Force $dir | Out-Null
$heavy = Join-Path $dir "mode-toggle-very-heavy.md"
@("# Very heavy mode-toggle diagnostic document", "") | Set-Content -Encoding UTF8 $heavy
1..160 | ForEach-Object {
  @(
    "## Section $_",
    'This paragraph is intentionally wide. Inline math: $S_{n} = \sum_{k=1}^{n} k^{2}$. wide-cell-text wide-cell-text wide-cell-text wide-cell-text wide-cell-text.',
    "",
    '| Column A | Column B | Column C | Column D |',
    '| --- | --- | --- | --- |',
    "| row $_ | wide-cell-text wide-cell-text | `$a_{$_}` | long text long text long text |",
    "",
    '```mermaid',
    'flowchart LR',
    "  A$_[Input $_] --> B$_[Render]",
    "  B$_ --> C$_[Preview]",
    '```',
    ""
  ) | Add-Content -Encoding UTF8 $heavy
}
Start-Process `
  -FilePath .\src\MarkMello.Applicate.Desktop\bin\Debug\net10.0\MarkMello.Applicate.exe `
  -ArgumentList $heavy
```

## Проверки

Начиная с узких проверок:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run check:renderer
npm --prefix src\MarkMello.Applicate.Desktop run build:renderer
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer
dotnet build MarkMello.sln --no-restore
dotnet test MarkMello.sln --no-restore --no-build -m:1 -- xunit.parallelizeTestCollections=false
git diff --check
```

Если менялись тесты и повторный `.NET` прогон с `--no-build` показывает старые assertions, перезапусти затронутый test project без `--no-build`.

## Визуальная диагностика

Для UI и renderer багов build/test не доказывают визуальную корректность. Нужен manual visual pass.

Правила захвата:

- Снимай все окно целиком, включая preview/right side и верхнюю правую кнопку закрытия.
- Если на кадре нет `X` в правом верхнем углу окна, capture считается кривым и не является доказательством.
- Если окно вышло за границы desktop, сначала перемести или maximized его внутрь видимой области.
- Не принимай left-only crop. Лучше снять full desktop, потом обрезать только после проверки, что обе стороны окна есть.
- Для переходов снимай несколько повторов: мусор может появляться не на каждом переключении.

Минимальный visual matrix для renderer/UI изменений:

| Сценарий | Что проверить |
| --- | --- |
| `Ctrl+E`: reading -> edit | Нет растянутого, сдвинутого или неподготовленного render слоя |
| `Ctrl+E`: edit -> reading | Нет мусора между layout states; финальный reading готов |
| Tab switch: light and heavy docs | Tab switch не стал медленным, TOC/minimap не пропадают |
| Theme dark/light | Renderer не сбрасывает документ и не уходит в blank pane |
| WebView focus | Hotkeys работают после клика в Avalonia и после клика внутри WebView |
| Document close | Нет stale preview/render мусора при закрытии вкладки |
| Minimap mode | `auto` может скрывать, `on` должен оставаться включенным |

## Что нельзя ломать при фиксе соседнего бага

- Исправление `Ctrl+E` не должно менять tab-switch pipeline.
- Исправление tab switching не должно менять edit/reading transition.
- Исправление theme sync не должно сбрасывать renderer document state.
- Исправление visual garbage не должно прятать root cause blanket-cover'ом без доказанной ownership-гипотезы.
- Любое изменение вокруг WebView reveal, native cover, screenshot cover, readiness quorum, minimap settle, TOC refresh или focus routing требует adjacent regression pass из таблицы выше.

## Claude и внешние review

Если нужен Claude/review loop, используй только file-based prompt и сохраняй stdout/stderr:

```powershell
$prompt = ".scratch\claude-prompts\review.md"
$stdout = ".scratch\claude-prompts\review.out.txt"
$stderr = ".scratch\claude-prompts\review.err.txt"
Get-Content -Raw $prompt |
  claude -p --model opus --effort max --permission-mode plan `
    --tools "Read,Grep,Glob" --output-format text `
    1> $stdout 2> $stderr
```

Claude verdict не заменяет локальную проверку. Если пользователь или визуальные кадры противоречат review, review считается невалидным для этого решения.

## Перед ответом пользователю

Сообщай только проверенное:

- текущий `HEAD` и dirty paths, если они важны;
- какие команды реально запускались;
- какие visual artifacts реально просмотрены;
- что осталось непроверенным.

Не пиши "готово", если проверен только один переход, один tab state или один theme state.

## Термины и сокращения

- Applicate: fork-specific desktop shell in this repository.
- Bundle: generated renderer output file, usually `RendererWeb/assets/renderer.js`.
- Claude: external model/provider used only through file-based prompts in this repository.
- Dirty tree: Git working tree with uncommitted changes.
- Renderer: WebView/Chromium Markdown rendering path.
- TOC: Table of Contents; document outline pane.
- UI: User Interface.
- VM: ViewModel; presentation-layer state object.
- WebView: embedded Chromium surface used by Applicate for rich Markdown rendering.
