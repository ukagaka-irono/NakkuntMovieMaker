cmake_minimum_required(VERSION 3.20)
project(nmm_audio VERSION 0.1.0 LANGUAGES C)

# miniaudio（再生と録音）を .NET から使うための薄い C ラッパー
add_library(nmm_audio SHARED src/nmm_audio.c)
target_include_directories(nmm_audio
    PUBLIC include
    PRIVATE third_party/miniaudio)
target_compile_definitions(nmm_audio PRIVATE NMM_AUDIO_BUILD)
set_target_properties(nmm_audio PROPERTIES
    C_STANDARD 11
    C_VISIBILITY_PRESET hidden)

if(MSVC)
    target_compile_options(nmm_audio PRIVATE /utf-8)
endif()

if(UNIX AND NOT APPLE)
    find_package(Threads REQUIRED)
    target_link_libraries(nmm_audio PRIVATE Threads::Threads ${CMAKE_DL_LIBS} m)
endif()

# 出力先（native/bin/<rid>/）に、ライブラリとライセンス表記を置く
install(TARGETS nmm_audio
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .)
install(FILES third_party/miniaudio/LICENSE
    DESTINATION licenses
    RENAME miniaudio-LICENSE.txt)
