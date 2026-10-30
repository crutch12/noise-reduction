#pragma once
#include <stdint.h>

#ifdef _WIN32
#define NR_BRIDGE_API extern "C" __declspec(dllexport)
#else
#define NR_BRIDGE_API extern "C" __attribute__((visibility("default")))
#endif

// мост для macOS и Linux: реальный микрофон, обработка, виртуальное устройство
// настройки в тех же битах, что control-файл на Windows: 1 включено, 2 срез гула, 8 DeepFilterNet, сила в дБ << 8

// названия устройств через \n, у устройства по умолчанию в начале *, возвращает нужный размер буфера
NR_BRIDGE_API int nr_bridge_list(int playback, char* buffer, int size);
// пустое имя значит устройство по умолчанию; models это папка с librnnoise и libnr_deepfilter
// на macOS выход можно задать как uid:<UID устройства>, так находится скрытый выход драйвера
// backend 0 системный, 1 пустой для тестов
NR_BRIDGE_API void* nr_bridge_start(const char* input, const char* output, const char* models, int backend);
NR_BRIDGE_API void nr_bridge_configure(void* bridge, uint32_t config);
NR_BRIDGE_API void nr_bridge_stats(void* bridge, uint64_t* frames, uint64_t* model_frames, uint64_t* errors);
NR_BRIDGE_API void nr_bridge_stop(void* bridge);
// обработка готовой записи 48 кГц моно тем же кодом, что и живой звук, задержка уже убрана
// возвращает 0, без модели 1 (тогда на выходе исходный звук)
NR_BRIDGE_API int nr_bridge_process(const char* models, uint32_t config, const float* input, float* output, int count);
// macOS: 1 если устройство с таким UID сейчас пишет какая-то программа, 0 если нет, -1 если не узнать
NR_BRIDGE_API int nr_bridge_in_use(const char* uid);
