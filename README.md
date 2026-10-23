# Noise Reduction

Прототип локального шумоподавления микрофона под Windows

Сейчас работают сравнение моделей на записи, нативное ядро RNNoise и тестовый APO для отдельного host
Системная обработка ещё не установлена, браузер, Telegram и Recorder пока не получают обработанный звук

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
В APO переключатель пока не подключён к системным настройкам

DLL намеренно не предоставляет регистрацию в Windows и принимает только тестовую инициализацию
Она предназначена для отдельного host, не готова к установке в audiodg
Нет CAPX-настроек, привязки к endpoint, поддержки произвольных форматов, подписанного пакета и механизма восстановления OEM-цепочек
Защита Windows Audio не отключается

## Источники

- [RNNoise](https://github.com/xiph/rnnoise)
- [Python packaging RNNoise](https://github.com/pengzhendong/pyrnnoise)
- [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet)
- [Windows APO](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects)

