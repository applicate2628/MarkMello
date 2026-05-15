![MarkMello](assets/cover.png)

# MarkMello Applicate

[English](README.en.md)

**MarkMello Applicate — форк MarkMello для быстрого чтения Markdown-файлов с улучшенным отображением технических документов и TeX-формул.**

Этот репозиторий является форком [upstream MarkMello](https://github.com/dartdavros/MarkMello).
Оригинальный проект принадлежит MarkMello contributors. Applicate-добавления
поддерживаются отдельно: Copyright (C) 2026 Dmitry Denisenko (@applicate2628).
Подробности см. в [NOTICE.md](NOTICE.md) и [FORK_CHANGES.md](FORK_CHANGES.md).

## Что умеет MarkMello

MarkMello Applicate сохраняет базовые возможности MarkMello:

- быстро открывать Markdown-файлы в режиме просмотра;
- настраивать удобный режим чтения: тему, размер шрифта, высоту строки и ширину области документа;
- при необходимости переходить в режим редактирования и вносить правки в файл.

Applicate-добавления:

- добавляют опциональный WebView/KaTeX renderer для inline- и display-формул TeX в Markdown;
- рендерят Markdown локально: документы, HTML, KaTeX assets и временные WebView-файлы остаются на машине пользователя, а remote image links в WebView заменяются placeholder-ами;
- сохраняют native renderer как fallback и как режим совместимости;
- добавляют гибкое изменение ширины чтения перетаскиванием края, сохраняя исходные пресеты Narrow, Medium и Wide;
- добавляют minimap для WebView renderer и сохраняют native minimap для native renderer;
- синхронизируют чтение, resize, theme, edit preview и переключение Native/WebView без пустого кадра;
- добавляют полосу вкладок над документом и multi-document модель: одновременно открыто несколько `.md` файлов, переключение кликом, закрытие крестиком, перетаскивание вкладок для смены порядка;
- запоминают список открытых документов и активную вкладку между запусками (`%AppData%/MarkMello/applicate-session.json`);
- принимают drag-and-drop файлов в окно: в режиме чтения — открытие как новой вкладки, в режиме редактирования — вставка в позицию каретки (изображения сохраняются рядом с документом и вставляются как относительная ссылка вида `![](images/name.png)`).

## Чем отличается от обычных Markdown-редакторов

MarkMello сначала открывает файл для чтения.

Редактирование не является основным режимом запуска: оно включается вручную, когда нужно внести правки.

## Установка

Скачайте актуальную сборку из раздела [Releases](../../releases/latest).

### Windows

1. Скачайте `MarkMello.Applicate-setup-win-x64.exe` или `MarkMello.Applicate-setup-win-arm64.exe`, в зависимости от архитектуры компьютера.
2. Запустите установщик.
3. Откройте MarkMello Applicate из меню Start или откройте `.md` файл через MarkMello Applicate.

### Другие платформы

MarkMello Applicate распространяется **только под Windows**. Renderer форка завязан на WebView2 (Microsoft Edge Chromium), у которого нет нативного аналога на macOS или Linux. Pre-built бинарей для других платформ нет и не планируется в обозримом будущем.

Если нужна upstream-версия MarkMello (без WebView2/Applicate-добавок) на других платформах — [соберите из исходников](#сборка-из-исходников). Сборка форка `MarkMello.Applicate.Desktop` на не-Windows не получится.

## Временные сборки без подписи разработчика

Текущие публичные сборки MarkMello Applicate временно распространяются без подписи разработчика. Из-за этого Windows может показать предупреждение при первом запуске.

Это временное ограничение distribution pipeline. Подпись разработчика будет добавлена в будущем.

### Windows: обход SmartScreen

Если Windows показывает предупреждение SmartScreen:

1. Нажмите `Подробнее`.
2. Нажмите `Выполнить в любом случае`.

Если Windows пометила скачанный файл как заблокированный:

1. Откройте свойства установочного файла.
2. Включите `Разблокировать`, если такой пункт доступен.
3. Примените изменения и запустите установщик снова.

## Сборка из исходников

Требуется .NET SDK 10. Для пересборки WebView renderer assets также нужен Node.js/npm.

```bash
dotnet restore ./MarkMello.sln
dotnet build ./MarkMello.sln
```

Если менялся TypeScript renderer:

```bash
npm --prefix ./src/MarkMello.Applicate.Desktop install
npm --prefix ./src/MarkMello.Applicate.Desktop run check:renderer
npm --prefix ./src/MarkMello.Applicate.Desktop run build:renderer
```

Запуск upstream-проекта:

```bash
dotnet run --project ./src/MarkMello.Desktop/MarkMello.Desktop.csproj
```

Запуск Applicate-форка:

```bash
dotnet run --project ./src/MarkMello.Applicate.Desktop/MarkMello.Applicate.Desktop.csproj
```

Открытие файла из командной строки:

```bash
dotnet run --project ./src/MarkMello.Applicate.Desktop/MarkMello.Applicate.Desktop.csproj -- ./sample.md
```

Сборка Windows-инсталлятора Applicate-форка описана в [packaging/README.md](packaging/README.md).

## Горячие клавиши

| Действие | Сочетание |
| --- | --- |
| Открыть файл | `Ctrl+O` |
| Переключить режим редактирования | `Ctrl+E` |
| Сохранить | `Ctrl+S` |
| Сохранить как | `Ctrl+Shift+S` |

## Лицензия

Проект распространяется по лицензии GPL-3.0.

См. файл [LICENSE](LICENSE).

## Термины и сокращения

- `Applicate`: fork-specific overlay с дополнительной поддержкой формул и reader-улучшениями.
- `GPL-3.0`: GNU General Public License version 3.
- `Markdown`: lightweight markup format для текстовой документации.
- `minimap`: боковая миниатюра документа для быстрой навигации.
- `renderer path`: путь обработки Markdown от parser model до UI-рендера.
- `TeX`: синтаксис математических формул, используемый Markdown math renderers.
- `upstream`: оригинальный репозиторий MarkMello, от которого сделан форк.
