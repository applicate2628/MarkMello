# MarkMello

[EN](#english) | [RU](#русский)

---

## English

> A fast, viewer-first Markdown reader for your desktop.

MarkMello opens local `.md` files quickly and keeps the document at the center of the experience. No workspace setup, no project tree, no sync account, and no editor overhead until you ask for it.

---

## Why it exists

Most Markdown tools assume you want an editor, a sidebar, a repo, and a lot of UI around the file. MarkMello is built for the simpler and more common case: you just want to open a Markdown document and read it comfortably.

- Fast startup and direct file opening
- A centered reading surface with document-wide text selection
- Local-first behavior with no network requirement to read your files
- Lazy edit mode instead of editor-first startup

## What it does

- Opens Markdown files from the file picker, drag and drop, or the command line
- Renders headings, paragraphs, lists, quotes, code blocks, tables, images, and links
- Persists reading preferences such as theme, font mode, font size, line height, and content width
- Supports a split edit mode with save, save as, and dirty-state handling
- Provides manual GitHub Releases update checks in `Settings -> Updates`

## Install

Download the latest assets from the [latest release](../../releases/latest).

### Temporary unsigned builds

Current Windows and macOS builds are temporarily distributed without a developer signature. This may cause Windows SmartScreen or macOS Gatekeeper to block the first launch.

Only install MarkMello from the official GitHub Releases page. Developer signing and notarization are planned and will be added in a future release.

### Windows

1. Download `MarkMello-setup-win-x64.exe` or `MarkMello-setup-win-arm64.exe`, depending on your machine.
2. Run the installer.
3. If Windows SmartScreen shows `Windows protected your PC`, click `More info`, then `Run anyway`.
4. If Windows blocks the downloaded file before launch, open file `Properties`, enable `Unblock` if the option is present, click `Apply`, then run the installer again.
5. Launch MarkMello from the Start menu or open a `.md` file with it.

### macOS

1. Download `MarkMello-macos-arm64.dmg` for Apple Silicon or `MarkMello-macos-x64.dmg` for Intel Macs.
2. Open the DMG.
3. Drag `MarkMello.app` into `Applications`.
4. Try to launch the app from `Applications`.
5. If macOS blocks the app, open `System Settings -> Privacy & Security`, scroll to the `Security` section, then click `Open Anyway` for MarkMello.
6. Confirm the warning dialog by clicking `Open`.

If macOS still shows that the app is damaged or cannot be opened, use this terminal command only for a MarkMello build downloaded from the official GitHub Releases page:

```bash
xattr -dr com.apple.quarantine /Applications/MarkMello.app
open /Applications/MarkMello.app
```

### Linux

If a Linux AppImage is attached to a release, install it like this:

```bash
chmod +x MarkMello-linux-x86_64.AppImage
./MarkMello-linux-x86_64.AppImage
```

If no Linux asset is published for the release you want, build from source instead.

## Open a file

You can open a document in three ways:

1. Open a `.md` file from your file manager
2. Drag a Markdown file onto the window
3. Press `Ctrl+O` or `Cmd+O` inside the app

Command-line activation also works:

```bash
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj -- ./sample.md
```

## Reading and editing

MarkMello starts as a reader. The reading surface is centered, text selection works across the whole document, and the chrome stays out of the way until you need it.

Reading preferences apply live and persist between launches:

- Theme: System, Light, Dark
- Font mode: Serif, Sans, Mono
- Font size
- Line height
- Content width

Edit mode is intentionally secondary. Press `Ctrl+E` or `Cmd+E` to open the split editor only when you need it.

## Keyboard shortcuts

- `Ctrl+N` / `Cmd+N` — create a new Markdown document
- `Ctrl+O` / `Cmd+O` — open a file
- `Ctrl+E` / `Cmd+E` — toggle edit mode
- `Ctrl+S` / `Cmd+S` — save
- `Ctrl+Shift+S` / `Cmd+Shift+S` — save as
- `Ctrl+R` / `Cmd+R` / `F5` — reload the current file
- `Ctrl+,` / `Cmd+,` — toggle reading preferences
- `Escape` — clear the current load error state

## Build from source

Prerequisites:

- .NET SDK 9.0 or newer

Build and run:

```bash
dotnet restore ./MarkMello.sln
dotnet build ./MarkMello.sln -c Debug
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj
```

Try the included sample document:

```bash
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj -- ./sample.md
```

Create a local Release build:

```bash
dotnet build ./MarkMello.sln -c Release
```

## Repository layout

```text
src/
├── MarkMello.Domain
├── MarkMello.Application
├── MarkMello.Infrastructure
├── MarkMello.Presentation
└── MarkMello.Desktop
```

## Packaging and release notes

- Packaging notes live in [packaging/README.md](packaging/README.md)
- Desktop release automation lives in [.github/workflows/release-windows.yml](.github/workflows/release-windows.yml)
- Windows installers and macOS DMGs are published through GitHub Releases
- The in-app updater checks GitHub Releases manually from `Settings -> Updates`

---

## Русский

> Быстрый Markdown-viewer для рабочего стола, в котором чтение важнее интерфейса.

MarkMello быстро открывает локальные `.md` файлы и оставляет документ в центре пользовательского опыта. Без workspace, дерева проекта, sync-аккаунта и editor-overhead до тех пор, пока пользователь явно не включит редактирование.

---

## Зачем он нужен

Большинство Markdown-инструментов исходят из того, что пользователю нужен редактор, боковая панель, репозиторий и много интерфейса вокруг файла. MarkMello сделан для более простого и частого сценария: открыть Markdown-документ и комфортно его прочитать.

- Быстрый запуск и прямое открытие файлов
- Центрированная область чтения с выделением текста по всему документу
- Local-first поведение без необходимости сети для чтения файлов
- Ленивый режим редактирования вместо editor-first запуска

## Что умеет приложение

- Открывает Markdown-файлы через file picker, drag and drop или command line
- Рендерит заголовки, параграфы, списки, цитаты, блоки кода, таблицы, изображения и ссылки
- Сохраняет настройки чтения: тему, режим шрифта, размер шрифта, line height и ширину контента
- Поддерживает split edit mode с save, save as и dirty-state handling
- Поддерживает ручную проверку обновлений через GitHub Releases в `Settings -> Updates`

## Установка

Скачайте нужный файл из [последнего релиза](../../releases/latest).

### Временные сборки без подписи разработчика

Текущие Windows и macOS сборки временно распространяются без подписи разработчика. Из-за этого Windows SmartScreen или macOS Gatekeeper могут заблокировать первый запуск.

Устанавливайте MarkMello только с официальной страницы GitHub Releases. Подпись разработчика и notarization будут добавлены в одном из следующих релизов.

### Windows

1. Скачайте `MarkMello-setup-win-x64.exe` или `MarkMello-setup-win-arm64.exe`, в зависимости от архитектуры компьютера.
2. Запустите установщик.
3. Если Windows SmartScreen показывает `Windows protected your PC`, нажмите `More info`, затем `Run anyway`.
4. Если Windows блокирует скачанный файл до запуска, откройте `Properties` файла, включите `Unblock`, если такой пункт есть, нажмите `Apply`, затем снова запустите установщик.
5. Запустите MarkMello из Start menu или откройте `.md` файл через MarkMello.

### macOS

1. Скачайте `MarkMello-macos-arm64.dmg` для Apple Silicon или `MarkMello-macos-x64.dmg` для Intel Mac.
2. Откройте DMG.
3. Перетащите `MarkMello.app` в `Applications`.
4. Попробуйте запустить приложение из `Applications`.
5. Если macOS заблокирует приложение, откройте `System Settings -> Privacy & Security`, прокрутите до секции `Security`, затем нажмите `Open Anyway` для MarkMello.
6. Подтвердите системное предупреждение кнопкой `Open`.

Если macOS всё ещё пишет, что приложение повреждено или не может быть открыто, используйте эту команду в терминале только для сборки MarkMello, скачанной с официальной страницы GitHub Releases:

```bash
xattr -dr com.apple.quarantine /Applications/MarkMello.app
open /Applications/MarkMello.app
```

### Linux

Если к релизу приложен Linux AppImage, запустите его так:

```bash
chmod +x MarkMello-linux-x86_64.AppImage
./MarkMello-linux-x86_64.AppImage
```

Если для нужного релиза Linux asset не опубликован, соберите приложение из исходников.

## Открытие файла

Документ можно открыть тремя способами:

1. Открыть `.md` файл из file manager
2. Перетащить Markdown-файл в окно приложения
3. Нажать `Ctrl+O` или `Cmd+O` внутри приложения

Command-line activation тоже поддерживается:

```bash
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj -- ./sample.md
```

## Чтение и редактирование

MarkMello запускается как reader. Область чтения центрирована, выделение текста работает по всему документу, а интерфейс не мешает до тех пор, пока он не нужен.

Настройки чтения применяются сразу и сохраняются между запусками:

- Theme: System, Light, Dark
- Font mode: Serif, Sans, Mono
- Font size
- Line height
- Content width

Edit mode намеренно вторичен. Нажмите `Ctrl+E` или `Cmd+E`, чтобы открыть split editor только тогда, когда он действительно нужен.

## Горячие клавиши

- `Ctrl+N` / `Cmd+N` — создать новый Markdown-документ
- `Ctrl+O` / `Cmd+O` — открыть файл
- `Ctrl+E` / `Cmd+E` — переключить edit mode
- `Ctrl+S` / `Cmd+S` — сохранить
- `Ctrl+Shift+S` / `Cmd+Shift+S` — сохранить как
- `Ctrl+R` / `Cmd+R` / `F5` — перезагрузить текущий файл
- `Ctrl+,` / `Cmd+,` — открыть/закрыть настройки чтения
- `Escape` — очистить текущее состояние ошибки загрузки

## Сборка из исходников

Требования:

- .NET SDK 9.0 или новее

Сборка и запуск:

```bash
dotnet restore ./MarkMello.sln
dotnet build ./MarkMello.sln -c Debug
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj
```

Запуск с тестовым документом из репозитория:

```bash
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj -- ./sample.md
```

Локальная Release-сборка:

```bash
dotnet build ./MarkMello.sln -c Release
```

## Структура репозитория

```text
src/
├── MarkMello.Domain
├── MarkMello.Application
├── MarkMello.Infrastructure
├── MarkMello.Presentation
└── MarkMello.Desktop
```

## Packaging и release notes

- Packaging notes находятся в [packaging/README.md](packaging/README.md)
- Desktop release automation находится в [.github/workflows/release-windows.yml](.github/workflows/release-windows.yml)
- Windows installers и macOS DMGs публикуются через GitHub Releases
- In-app updater вручную проверяет GitHub Releases через `Settings -> Updates`
