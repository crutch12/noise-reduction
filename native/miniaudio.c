// реализация miniaudio отдельным файлом, чтобы не собирать её вместе с мостом
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#include "miniaudio.h"
