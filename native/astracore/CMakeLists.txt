cmake_minimum_required(VERSION 3.25)
project(AstraCoreNative VERSION 0.1.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN YES)

find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBAV REQUIRED IMPORTED_TARGET
    libavformat
    libavcodec
    libavutil
    libswresample
    libavfilter)

add_library(AstraCore.Native SHARED src/astracore.c)
target_compile_definitions(AstraCore.Native PRIVATE ASTRACORE_BUILD)
target_include_directories(AstraCore.Native PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>)
target_link_libraries(AstraCore.Native PRIVATE PkgConfig::LIBAV)
set_target_properties(AstraCore.Native PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
    OUTPUT_NAME "AstraCore.Native")

if(WIN32)
    set_target_properties(AstraCore.Native PROPERTIES PREFIX "")
elseif(APPLE)
    set_target_properties(AstraCore.Native PROPERTIES
        PREFIX "lib"
        INSTALL_RPATH "@loader_path"
        BUILD_WITH_INSTALL_RPATH TRUE)
else()
    set_target_properties(AstraCore.Native PROPERTIES
        PREFIX "lib"
        INSTALL_RPATH "$ORIGIN"
        BUILD_WITH_INSTALL_RPATH TRUE)
endif()

include(CTest)
if(BUILD_TESTING)
    add_executable(astracore_probe_smoke tests/probe_smoke.c)
    target_link_libraries(astracore_probe_smoke PRIVATE AstraCore.Native)

    add_executable(astracore_test_editing tests/test_editing.c)
    target_link_libraries(astracore_test_editing PRIVATE AstraCore.Native)

    if(APPLE)
        set_target_properties(astracore_probe_smoke PROPERTIES
            INSTALL_RPATH "@executable_path"
            BUILD_WITH_INSTALL_RPATH TRUE)
        set_target_properties(astracore_test_editing PROPERTIES
            INSTALL_RPATH "@executable_path"
            BUILD_WITH_INSTALL_RPATH TRUE)
    elseif(UNIX)
        set_target_properties(astracore_probe_smoke PROPERTIES
            INSTALL_RPATH "$ORIGIN"
            BUILD_WITH_INSTALL_RPATH TRUE)
        set_target_properties(astracore_test_editing PROPERTIES
            INSTALL_RPATH "$ORIGIN"
            BUILD_WITH_INSTALL_RPATH TRUE)
    endif()

    add_test(NAME astracore_probe_invalid_path COMMAND astracore_probe_smoke)
    add_test(NAME astracore_editing_tests COMMAND astracore_test_editing)
endif()

install(TARGETS AstraCore.Native
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib)

install(FILES include/astracore.h DESTINATION include)

configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/astracore.pc.in
    ${CMAKE_CURRENT_BINARY_DIR}/astracore.pc
    @ONLY
)
install(FILES ${CMAKE_CURRENT_BINARY_DIR}/astracore.pc DESTINATION lib/pkgconfig)
