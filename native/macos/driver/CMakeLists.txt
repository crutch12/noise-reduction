cmake_minimum_required(VERSION 3.20)
project(NoiseReduction CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "")
set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "")
set(DRIVER_VERSION "1.0.0" CACHE STRING "")
set(LIBASPL_SHA256 428f1ed5cecea725ac4e2db3ab65889a72d133cff8d23ac21b07bf6e2f0561e4)

# libASPL v3.1.2 под MIT, архив сверяется по хешу
include(ExternalProject)
ExternalProject_Add(libASPL
  URL https://github.com/gavv/libASPL/archive/refs/tags/v3.1.2.tar.gz
  URL_HASH SHA256=${LIBASPL_SHA256}
  CMAKE_ARGS -DCMAKE_INSTALL_PREFIX=<INSTALL_DIR> -DCMAKE_OSX_ARCHITECTURES=arm64$<SEMICOLON>x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=${CMAKE_OSX_DEPLOYMENT_TARGET} -DBUILD_TESTING=OFF
  INSTALL_DIR ${CMAKE_CURRENT_BINARY_DIR}/libASPL-prefix
)

add_library(NoiseReduction MODULE Driver.cpp)
add_dependencies(NoiseReduction libASPL)
target_include_directories(NoiseReduction PRIVATE ${CMAKE_CURRENT_BINARY_DIR}/libASPL-prefix/include)
target_link_libraries(NoiseReduction PRIVATE ${CMAKE_CURRENT_BINARY_DIR}/libASPL-prefix/lib/libASPL.a "-framework CoreFoundation")
set_target_properties(NoiseReduction PROPERTIES
  OUTPUT_NAME NoiseReduction
  BUNDLE true
  BUNDLE_EXTENSION driver
  PREFIX ""
  SUFFIX ""
  MACOSX_BUNDLE ON
  MACOSX_BUNDLE_INFO_PLIST ${CMAKE_CURRENT_SOURCE_DIR}/Info.plist.in
  MACOSX_BUNDLE_BUNDLE_NAME NoiseReduction
  MACOSX_BUNDLE_BUNDLE_VERSION ${DRIVER_VERSION}
  MACOSX_BUNDLE_SHORT_VERSION_STRING ${DRIVER_VERSION}
  MACOSX_BUNDLE_GUI_IDENTIFIER com.github.crutch12.noisereduction.driver
  MACOSX_BUNDLE_COPYRIGHT "Copyright (c) Noise Reduction authors"
)
# без сертификата Apple подписываем ad-hoc, иначе coreaudiod на Apple Silicon не загрузит драйвер
add_custom_command(TARGET NoiseReduction POST_BUILD
  COMMAND codesign --force --sign - ${CMAKE_CURRENT_BINARY_DIR}/NoiseReduction.driver
  VERBATIM)
