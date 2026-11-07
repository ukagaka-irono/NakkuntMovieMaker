cmake_minimum_required(VERSION 3.20)
project(nmm_ocio VERSION 0.1.0 LANGUAGES C CXX)

# OpenColorIO を .NET から使うための薄い C ラッパー。
# OCIO と依存するライブラリは、先に deps/（tools/build-native が呼ぶ）で静的ライブラリにして、CMAKE_PREFIX_PATH に置いておく
#   cmake -S native/nmm_ocio -B <ビルド先> -DCMAKE_PREFIX_PATH=<deps の置き場所> -DNMM_DOWNLOADS=<native/downloads>

set(NMM_DOWNLOADS "" CACHE PATH "tools/fetch-native がソースを展開した場所（native/downloads。ライセンスの全文を取る）")

# Windows では C/C++ のランタイムを静的にリンクする（deps と同じ）
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)

# OCIO の構成（OpenColorIOConfig.cmake）が依存を探すときに、OS の共有ライブラリではなく、deps で作った静的ライブラリを使わせる。
# zlib は deps と同じく、静的ライブラリの場所を直接渡す（MSVC では zs.lib、ほかは libz.a）
set(ZLIB_USE_STATIC_LIBS ON)
set(expat_STATIC_LIBRARY ON)
set(yaml-cpp_STATIC_LIBRARY ON)
set(Imath_STATIC_LIBRARY ON)
set(pystring_STATIC_LIBRARY ON)
set(minizip-ng_STATIC_LIBRARY ON)
find_library(ZLIB_LIBRARY NAMES zs libz.a PATH_SUFFIXES lib NO_CMAKE_ENVIRONMENT_PATH NO_SYSTEM_ENVIRONMENT_PATH NO_CMAKE_SYSTEM_PATH)
find_path(ZLIB_INCLUDE_DIR zlib.h PATH_SUFFIXES include NO_CMAKE_ENVIRONMENT_PATH NO_SYSTEM_ENVIRONMENT_PATH NO_CMAKE_SYSTEM_PATH)

find_package(OpenColorIO 2.5.2 EXACT CONFIG REQUIRED)

add_library(nmm_ocio SHARED src/nmm_ocio.cpp)
target_include_directories(nmm_ocio PUBLIC include)
target_compile_definitions(nmm_ocio PRIVATE NMM_OCIO_BUILD)
target_link_libraries(nmm_ocio PRIVATE OpenColorIO::OpenColorIO)
set_target_properties(nmm_ocio PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

if(MSVC)
    target_compile_options(nmm_ocio PRIVATE /utf-8 /EHsc)
elseif(APPLE)
    # 静的にリンクした OCIO などの記号を外に出さない（nmm_ocio_* だけを出す）
    target_link_options(nmm_ocio PRIVATE "LINKER:-exported_symbol,_nmm_ocio_*")
else()
    find_package(Threads REQUIRED)
    target_link_libraries(nmm_ocio PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
    target_link_options(nmm_ocio PRIVATE "LINKER:--exclude-libs,ALL" "LINKER:--no-undefined")
endif()

# 組み込みの ACES の構成を読めるかの小さなテスト（tools/build-native が ctest で実行する）
include(CTest)
if(BUILD_TESTING)
    add_executable(nmm_ocio_smoke tests/smoke.c)
    target_link_libraries(nmm_ocio_smoke PRIVATE nmm_ocio)
    if(MSVC)
        target_compile_options(nmm_ocio_smoke PRIVATE /utf-8)
    endif()
    add_test(NAME nmm_ocio_smoke COMMAND nmm_ocio_smoke)
endif()

# 出力先（native/bin/<rid>/）に、ライブラリとライセンスの全文を置く。
# 組み込んだ部品と、そのライセンスのファイルは tools/native-deps.json の ocio.sources から取る
install(TARGETS nmm_ocio
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .)

if(NMM_DOWNLOADS)
    file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../../tools/native-deps.json" deps_json)
    string(JSON source_count LENGTH "${deps_json}" ocio sources)
    math(EXPR last "${source_count} - 1")
    foreach(i RANGE ${last})
        string(JSON name GET "${deps_json}" ocio sources ${i} name)
        string(JSON license_file GET "${deps_json}" ocio sources ${i} licenseFile)
        install(FILES "${NMM_DOWNLOADS}/src-ocio-${name}/${license_file}"
            DESTINATION licenses
            RENAME ${name}-LICENSE.txt)
    endforeach()
    # OCIO が取り込んでいる部品（xxHash・SampleICC など）のライセンス
    install(FILES "${NMM_DOWNLOADS}/src-ocio-opencolorio/THIRD-PARTY.md"
        DESTINATION licenses
        RENAME opencolorio-THIRD-PARTY.txt)
endif()
