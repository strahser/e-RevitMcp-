# RevitMcp — мост OpenCode ↔ Revit 2024

Позволяет агенту (OpenCode) самостоятельно: открыть `.rvt` файл, прочитать
информацию о документе/помещениях/уровнях и запустить окна плагина
`HeatLossRevit4` — без ручных кликов в Revit.

## Архитектура

```
OpenCode --stdio--> mcp-server/server.py --HTTP 127.0.0.1:17871--> RevitMcp.Addin (внутри Revit) --> Revit API
```

* **RevitMcp.Addin** (C#, net48, Revit 2024) — `IExternalApplication`, держит
  `HttpListener` на `127.0.0.1:17871` (порт — `REVIT_MCP_PORT`), все вызовы
  Revit API выполняет на главном потоке через `ExternalEvent`.
* **mcp-server/server.py** (Python, `mcp>=2.0`, только stdlib+`mcp`) —
  stdio MCP-сервер с 8 инструментами. Если мост недоступен, `revit_open_document`
  сам запускает `Revit.exe <файл>` и ждёт готовности.

## Установка (один раз)

1. Собрать аддин:
   `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" RevitMcp.Addin\RevitMcp.Addin.csproj /t:Restore,Build /p:Configuration=Release.R24 /p:Platform="Any CPU"`
2. Разложить в Revit:
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install_addin.ps1`
3. Python-зависимость: `pip install -r mcp-server\requirements.txt` (уже стоит `mcp 2.0.0`)
4. Подключить к OpenCode — добавить в `opencode.json` (пример в `opencode.json.example`):
   ```json
   "mcp": {
     "revit-mcp": {
       "type": "local",
       "command": ["python", "-X", "utf8", "E:\\ПлагиныРевит\\RevitMcp\\mcp-server\\server.py"],
       "enabled": true
     }
   }
   ```
5. Перезапустить Revit 2024 (подхватит `RevitMcp.addin`), затем:
   `revit_open_document` с путём `E:\ПлагиныРевит\Данные теплотехнический расчет\newBuilding\HvackFinal.rvt`.

## Инструменты

| MCP-инструмент | HTTP | Назначение |
|---|---|---|
| `revit_ping` | `GET /ping` | жив ли мост, версия Revit |
| `revit_open_document` | `POST /open` | открыть `.rvt` (+ автозапуск Revit) |
| `revit_close_document` | `POST /close` | закрыть активный документ |
| `revit_doc_info` | `GET /doc` | заголовок/путь/счётчики помещений |
| `revit_list_rooms` | `GET /rooms` | помещения + MEP-пространства (пагинация) |
| `revit_list_levels` | `GET /levels` | уровни |
| `revit_commands` | `GET /commands` | ключи команд HeatLoss |
| `revit_run_heatloss` | `POST /run` | запустить окно плагина HeatLoss |

Ключи `revit_run_heatloss`: `settings`, `modeling`, `calculations` (= по умолчанию),
`main`, `reports`, `tools`, `about`, `snapshot`.

## Диагностика

* Лог аддина: `%TEMP%\RevitMcp.log`
* `revit_ping` падает с `RevitDown` — Revit не запущен или аддин не загрузился
  (проверить `%AppData%\Autodesk\Revit\Addins\2024\RevitMcp.addin`).
* `POST /run` требует загруженный `MainAppHeatLoss.dll` (плагин HeatLoss установлен).

## GitHub

Репозитория пока нет (локальный проект `E:\ПлагиныРевит\RevitMcp\`).
Если нужен `git init + push` — скажите, создам репозиторий на GitHub.
