cmake_minimum_required(VERSION 3.20)
project(nmm_ocio_deps NONE)

# OpenColorIO と、それが依存するライブラリを、tools/fetch-native で取得したソースから静的ライブラリ（PIC）にビルドして、
# CMAKE_INSTALL_PREFIX に置く（native/nmm_ocio が find_package で使う）。ビルド中には何もダウンロードしない。
#   cmake -S native/nmm_ocio/deps -B <ビルド先> -DNMM_DOWNLOADS=<native/downloads> -DCMAKE_INSTALL_PREFIX=<置き場所>
#   cmake --build <ビルド先> --config Release
# tools/build-native.{ps1,sh} から呼ぶ。オプションは OCIO の share/cmake/modules/install/Install*.cmake に合わせている

include(ExternalProject)

set(NMM_DOWNLOADS "" CACHE PATH "tools/fetch-native がソースを展開した場所（native/downloads）")
if(NOT IS_DIRECTORY "${NMM_DOWNLOADS}/src-ocio-opencolorio")
    message(FATAL_ERROR "OpenColorIO のソースがありません（${NMM_DOWNLOADS}）。先に tools/fetch-native を実行してください")
endif()

set(prefix "${CMAKE_INSTALL_PREFIX}")

set(common_args
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_INSTALL_PREFIX=${prefix}
    -DCMAKE_PREFIX_PATH=${prefix}
    -DCMAKE_INSTALL_LIBDIR=lib
    -DCMAKE_INSTALL_MESSAGE=NEVER
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    -DCMAKE_C_VISIBILITY_PRESET=hidden
    -DCMAKE_CXX_VISIBILITY_PRESET=hidden
    -DCMAKE_VISIBILITY_INLINES_HIDDEN=ON
    -DCMAKE_CXX_STANDARD=17
    -DBUILD_SHARED_LIBS=OFF
    -DBUILD_TESTING=OFF
    # Windows では C/C++ のランタイムを静的にリンクする（nmm_ocio.dll が MSVCP140.dll などを必要としないように）。
    # 古い cmake_minimum_required の部品（yaml-cpp）でも効くように、CMP0091 を NEW にする
    -DCMAKE_POLICY_DEFAULT_CMP0091=NEW
    -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded)
if(WIN32)
    # ソースは UTF-8。日本語の Windows の MSVC は既定で CP932 として読み、コメントの文字で壊れる（expat など）
    list(APPEND common_args -DCMAKE_C_FLAGS_INIT=/utf-8 -DCMAKE_CXX_FLAGS_INIT=/utf-8)
elseif(APPLE)
    list(APPEND common_args
        -DCMAKE_OSX_ARCHITECTURES=${CMAKE_OSX_ARCHITECTURES}
        -DCMAKE_OSX_DEPLOYMENT_TARGET=${CMAKE_OSX_DEPLOYMENT_TARGET})
endif()

# name: native/downloads/src-ocio-<name> のソースを、引数の CMake オプションでビルドして prefix に置く
function(nmm_ocio_dependency name)
    cmake_parse_arguments(arg "" "SOURCE_DIR" "DEPENDS;CMAKE_ARGS" ${ARGN})
    if(NOT arg_SOURCE_DIR)
        set(arg_SOURCE_DIR "${NMM_DOWNLOADS}/src-ocio-${name}")
    endif()
    ExternalProject_Add(${name}
        SOURCE_DIR "${arg_SOURCE_DIR}"
        BINARY_DIR "${CMAKE_BINARY_DIR}/${name}"
        INSTALL_DIR "${prefix}"
        DOWNLOAD_COMMAND ""
        UPDATE_COMMAND ""
        CMAKE_ARGS ${common_args} ${arg_CMAKE_ARGS}
        # 並列の数は、環境変数 CMAKE_BUILD_PARALLEL_LEVEL で決める（tools/build-native が設定する）
        BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release
        INSTALL_COMMAND ${CMAKE_COMMAND} --install <BINARY_DIR> --config Release
        DEPENDS ${arg_DEPENDS}
        LOG_CONFIGURE ON
        LOG_BUILD ON
        LOG_INSTALL ON
        LOG_OUTPUT_ON_FAILURE ON)
endfunction()

# zlib 1.3.2 が置く ZLIBConfig.cmake は、静的ライブラリだけのときにも共有ライブラリの部分を読もうとして失敗するので、
# CMake の FindZLIB に、静的ライブラリの場所を直接渡す（MSVC では zs.lib、ほかは libz.a）
if(MSVC)
    set(zlib_static_library "${prefix}/lib/zs.lib")
else()
    set(zlib_static_library "${prefix}/lib/libz.a")
endif()
set(zlib_args
    -DZLIB_ROOT=${prefix}
    -DZLIB_USE_STATIC_LIBS=ON
    -DZLIB_INCLUDE_DIR=${prefix}/include
    -DZLIB_LIBRARY=${zlib_static_library})

nmm_ocio_dependency(zlib CMAKE_ARGS
    -DZLIB_BUILD_SHARED=OFF
    -DZLIB_BUILD_STATIC=ON
    -DZLIB_BUILD_TESTING=OFF)

nmm_ocio_dependency(expat CMAKE_ARGS
    -DEXPAT_BUILD_DOCS=OFF
    -DEXPAT_BUILD_EXAMPLES=OFF
    -DEXPAT_BUILD_TESTS=OFF
    -DEXPAT_BUILD_TOOLS=OFF
    -DEXPAT_BUILD_FUZZERS=OFF
    -DEXPAT_BUILD_PKGCONFIG=OFF
    -DEXPAT_SHARED_LIBS=OFF
    -DEXPAT_MSVC_STATIC_CRT=ON)

# yaml-cpp 0.8.0 の emitterutils.cpp は <cstdint> を読み込まずに uint16_t を使うので、GCC 13 以降でビルドできない。
# ソースは変えずに、コンパイラの -include で読み込ませる（MSVC は別のヘッダー経由で読むので要らない）
if(WIN32)
    set(yaml_cpp_flags "")
else()
    set(yaml_cpp_flags "-DCMAKE_CXX_FLAGS=-include cstdint")
endif()
nmm_ocio_dependency(yaml-cpp CMAKE_ARGS
    ${yaml_cpp_flags}
    -DYAML_BUILD_SHARED_LIBS=OFF
    -DYAML_CPP_BUILD_TESTS=OFF
    -DYAML_CPP_BUILD_TOOLS=OFF
    -DYAML_CPP_BUILD_CONTRIB=OFF
    -DYAML_CPP_FORMAT_SOURCE=OFF
    -DYAML_CPP_INSTALL=ON
    -DYAML_MSVC_SHARED_RT=OFF)

nmm_ocio_dependency(imath CMAKE_ARGS
    -DPYTHON=OFF
    -DDOCS=OFF
    -DIMATH_INSTALL_PKG_CONFIG=OFF
    -DIMATH_HALF_USE_LOOKUP_TABLE=OFF)

# pystring の CMakeLists.txt は共有ライブラリしか作らず、ヘッダーも置かないので、OCIO の Buildpystring.cmake を使う
set(pystring_source "${CMAKE_BINARY_DIR}/pystring-src")
file(COPY "${NMM_DOWNLOADS}/src-ocio-pystring/pystring.cpp" "${NMM_DOWNLOADS}/src-ocio-pystring/pystring.h"
    DESTINATION "${pystring_source}")
# （Buildpystring.cmake は project() を cmake_minimum_required より先に呼ぶので、CMAKE_MSVC_RUNTIME_LIBRARY が効かない。先頭に足す）
file(READ "${NMM_DOWNLOADS}/src-ocio-opencolorio/share/cmake/projects/Buildpystring.cmake" pystring_cmake)
file(WRITE "${pystring_source}/CMakeLists.txt.new" "cmake_minimum_required(VERSION 3.10)\n${pystring_cmake}")
configure_file("${pystring_source}/CMakeLists.txt.new" "${pystring_source}/CMakeLists.txt" COPYONLY)
nmm_ocio_dependency(pystring SOURCE_DIR "${pystring_source}")

nmm_ocio_dependency(minizip-ng DEPENDS zlib CMAKE_ARGS
    ${zlib_args}
    -DMZ_COMPAT=OFF
    -DMZ_ZLIB=ON
    -DMZ_BZIP2=OFF
    -DMZ_LZMA=OFF
    -DMZ_ZSTD=OFF
    -DMZ_LIBCOMP=OFF
    -DMZ_PKCRYPT=OFF
    -DMZ_WZAES=OFF
    -DMZ_OPENSSL=OFF
    -DMZ_LIBBSD=OFF
    -DMZ_ICONV=OFF
    -DMZ_SIGNING=OFF
    -DMZ_FETCH_LIBS=OFF
    -DMZ_FORCE_FETCH_LIBS=OFF
    -DMZ_BUILD_TESTS=OFF
    -DMZ_BUILD_UNIT_TESTS=OFF)

# OCIO 本体。足りない依存をダウンロードさせない（OCIO_INSTALL_EXT_PACKAGES=NONE）。
# アプリ・Python・Java・テスト・GPU のテスト・文書は作らない。
# Apple の arm64 の SIMD（sse2neon）は、構成のときにダウンロードするので使わない（LUT を焼くだけなので速さは要らない）
nmm_ocio_dependency(opencolorio DEPENDS zlib expat yaml-cpp imath pystring minizip-ng CMAKE_ARGS
    ${zlib_args}
    -DOCIO_INSTALL_EXT_PACKAGES=NONE
    -DOCIO_BUILD_APPS=OFF
    -DOCIO_BUILD_TESTS=OFF
    -DOCIO_BUILD_GPU_TESTS=OFF
    -DOCIO_BUILD_PYTHON=OFF
    -DOCIO_BUILD_JAVA=OFF
    -DOCIO_BUILD_DOCS=OFF
    -DOCIO_BUILD_OPENFX=OFF
    -DOCIO_BUILD_NUKE=OFF
    -DOCIO_USE_SSE2NEON=OFF
    -DOCIO_WARNING_AS_ERROR=OFF
    -Dexpat_STATIC_LIBRARY=ON
    -Dyaml-cpp_STATIC_LIBRARY=ON
    -DImath_STATIC_LIBRARY=ON
    -Dpystring_STATIC_LIBRARY=ON
    -Dminizip-ng_STATIC_LIBRARY=ON)
