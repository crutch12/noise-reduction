# Noise Reduction

Windows-приложение для шумоподавления реальных микрофонов без виртуального входа

Собраны приложение с треем, профилями, автозапуском, сравнением записи, нативный APO и EXE-установщик
Системная загрузка APO на текущем ПК пока не подтверждена, это экспериментальная сборка
Обычная COM-активация и отдельные тесты проходят, но аудиограф Windows пока не вызывает модель
Защита аудиопроцесса Windows остаётся включённой

## Приложение

Установщик: `dist/setup/NoiseReduction.Setup.exe`
Настройки открываются через ярлык NoiseReduction в меню Пуск и иконку в трее
Запуск без установки: `dist/app/NoiseReduction.exe`

- Глобальный выключатель и настройки отдельно для каждого реального микрофона
- Сила подавления от 0 до 60 дБ, исходно 24 дБ
- Автозапуск включается и отключается в настройках
- Крестик сворачивает в трей, Выход отключает вычисление модели
- Сравнение записывает выбранный микрофон с bypass нашего эффекта и обрабатывает запись локально
- Статус требует продвижения счётчика модели в настоящем аудиографе
- Удаление через установленные приложения Windows восстанавливает принадлежащие нам изменения

Профили хранятся в LocalAppData/NoiseReduction, конфигурация и журнал восстановления в ProgramData/NoiseReduction
Установщик не включает личные записи и зависимости Python

## Послушать результат

После сборки открыть `dist/comparison/NoiseReduction.Compare.exe`
Переключение варианта сохраняет позицию записи
Варианты с пометкой "мягче" сохраняют небольшую долю исходного сигнала вместо максимально глубокого подавления
Во всех вариантах одинаковое входное усиление, индивидуальная нормализация не применяется
Длительность и задержка выровнены для прослушивания

Папка `dist/comparison/audio` содержит личную запись, не распространять её вместе с будущей программой

## Повторить сравнение

Требуются Windows x64, PowerShell, uv и ffmpeg в PATH
Использован ffmpeg из установленного на ПК пакета, точную версию фиксирует команда `ffmpeg -version`
При отсутствии ffmpeg: `winget install --id Gyan.FFmpeg --exact`

```powershell
./tools/setup-lab.ps1
./.local/venv/Scripts/python.exe tools/audio_bench.py my_mic_record.mp3
./.local/venv/Scripts/python.exe -m pytest tools/tests -q
./tools/build-audition.ps1
```

DeepFilterNet CLI закреплён на версии 0.5.6 с проверкой SHA-256
RNNoise используется из pyrnnoise 0.4.5 через float C ABI, без преобразования результата в int16 внутри сторонней обёртки
Python нужен только для лабораторного сравнения, нативное ядро от него не зависит
Отчёт `.local/comparison/report.json` содержит времена, уровни, задержки и хеши бинарников
Скорость обработки файла не равна задержке микрофона и не доказывает соблюдение сроков обработки под нагрузкой

## Нативная сборка

Требуются VS2022 Build Tools с C++ x64, Windows SDK 26100 и компонентом C++ ATL для тестового media-type host
Сборка использует стандартную папку Build Tools в Program Files (x86)

```powershell
cmd /c native\build.cmd
Copy-Item .local/venv/Lib/site-packages/pyrnnoise/rnnoise.dll build/rnnoise.dll
./build/processor_test.exe (Resolve-Path build/rnnoise.dll).Path
./build/apo_test.exe (Resolve-Path build/NoiseReduction.Apo.dll).Path
```

Полная проверка после подготовки записи: `./tools/verify.ps1`
Точные форматы подключённых микрофонов: `./build/device_probe.exe`

Первый нативный вариант поддерживает от 1 до 8 каналов float32 48 кГц, каждый канал обрабатывается отдельно
Задержка 30 мс: 20 мс у закреплённой модели RNNoise и 10 мс у адаптера кадров
Отключение в ядре плавно переводит звук на исходный сигнал с той же задержкой, затем останавливает вычисление модели
APO принимает Windows initialization v1/v2/v3, поддерживает управление эффектом и профилем endpoint

```powershell
./tools/build-app.ps1 -Test -NativeTest -UiTest
./tools/build-installer.ps1
```

Полная проверка включает native, COM aggregation, буферы, controls/notifications, sandbox реестра, UI и installer self-test
UI-тесты не записывают микрофон и восстанавливают временное значение автозапуска
Настоящие установка, обновление и удаление проверены отдельно от sandbox

Пока не подтверждены загрузка в audiodg, браузер/Telegram/Recorder, сон и физическое переподключение
RAW/exclusive/ASIO и частоты кроме 48 кГц не подтверждены
Подписанного релиза и подтверждения Windows HLK нет
Защита аудио и хранилища сертификатов автоматически не меняются
Естественность голоса нужно оценивать на слух, численные тесты её не заменяют

## Источники

- [RNNoise](https://github.com/xiph/rnnoise)
- [Python packaging RNNoise](https://github.com/pengzhendong/pyrnnoise)
- [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet)
- [Windows APO](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects)

Лицензии компонентов включены в установщик
