/* LAME 4.0 の config.h（macOS / Apple Silicon 用に手書き）。
 * 配布物の configure スクリプトは実行せず、config.h.in の項目のうち
 * macOS の標準 C 環境で成り立つものだけを定義している。
 * デコーダー（mpglib）、SIMD（x86 の SSE/NASM）、フロントエンドは使わない。 */
#ifndef LAME_CONFIG_H
#define LAME_CONFIG_H

/* SwiftPM のデバッグビルドは -DDEBUG を付けるが、LAME では DEBUG が診断出力を有効にするため無効にする */
#undef DEBUG

#define STDC_HEADERS 1
#define HAVE_ERRNO_H 1
#define HAVE_FCNTL_H 1
#define HAVE_INTTYPES_H 1
#define HAVE_LIMITS_H 1
#define HAVE_STDINT_H 1
#define HAVE_STDIO_H 1
#define HAVE_STDLIB_H 1
#define HAVE_STRINGS_H 1
#define HAVE_STRING_H 1
#define HAVE_SYS_STAT_H 1
#define HAVE_SYS_TYPES_H 1
#define HAVE_UNISTD_H 1

#define HAVE_INT8_T 1
#define HAVE_INT16_T 1
#define HAVE_INT32_T 1
#define HAVE_INT64_T 1
#define HAVE_UINT8_T 1
#define HAVE_UINT16_T 1
#define HAVE_UINT32_T 1
#define HAVE_UINT64_T 1
#define A_INT32_T int
#define A_INT64_T long
#define A_UINT32_T unsigned int
#define A_UINT64_T unsigned long

#define SIZEOF_SHORT 2
#define SIZEOF_UNSIGNED_SHORT 2
#define SIZEOF_INT 4
#define SIZEOF_UNSIGNED_INT 4
#define SIZEOF_LONG 8
#define SIZEOF_UNSIGNED_LONG 8
#define SIZEOF_LONG_LONG 8
#define SIZEOF_UNSIGNED_LONG_LONG 8
#define SIZEOF_FLOAT 4
#define SIZEOF_DOUBLE 8
#define SIZEOF_LONG_DOUBLE 8

/* config.h.in と同じく、浮動小数点型の別名を定義する */
typedef float ieee754_float32_t;
typedef double ieee754_float64_t;
typedef long double ieee854_float80_t;

#define LAME_LIBRARY_BUILD 1
#define PACKAGE "lame"
#define PACKAGE_NAME "lame"
#define PACKAGE_VERSION "4.0"
#define VERSION "4.0"

#endif /* LAME_CONFIG_H */
