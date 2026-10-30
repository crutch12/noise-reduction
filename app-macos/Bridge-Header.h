#include <stdint.h>

// то же, что native/bridge.h, только без extern "C": Swift читает заголовок как C
int nr_bridge_list(int playback, char* buffer, int size);
void* nr_bridge_start(const char* input, const char* output, const char* models, int backend);
void nr_bridge_configure(void* bridge, uint32_t config);
void nr_bridge_stats(void* bridge, uint64_t* frames, uint64_t* model_frames, uint64_t* errors);
void nr_bridge_stop(void* bridge);
int nr_bridge_in_use(const char* uid);
