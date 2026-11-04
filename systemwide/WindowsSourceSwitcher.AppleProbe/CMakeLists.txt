cmake_minimum_required(VERSION 3.24)
project(DX5AppleSourceProbe VERSION 0.1.0 LANGUAGES C CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The experimental probe supports Windows x64 only")
endif()
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Vendored unmodified MinHook v1.3.4, pinned in third_party/minhook/UPSTREAM_REVISION.txt.
# No network download or third-party build script is needed by this project.
add_library(probe_minhook STATIC third_party/minhook/src/buffer.c third_party/minhook/src/hook.c
  third_party/minhook/src/trampoline.c third_party/minhook/src/hde/hde64.c)
target_include_directories(probe_minhook PUBLIC third_party/minhook/include PRIVATE third_party/minhook/src)

add_library(apple_source_probe SHARED src/ProbeDll.cpp)
target_include_directories(apple_source_probe PRIVATE include)
target_link_libraries(apple_source_probe PRIVATE probe_minhook advapi32)
set_target_properties(apple_source_probe PROPERTIES OUTPUT_NAME "dx5-apple-source-probe")

add_executable(apple_source_probe_loader src/ProbeLoader.cpp)
target_include_directories(apple_source_probe_loader PRIVATE include)
target_link_libraries(apple_source_probe_loader PRIVATE advapi32 bcrypt)
set_target_properties(apple_source_probe_loader PROPERTIES OUTPUT_NAME "dx5-apple-source-probe")

enable_testing()
add_executable(source_format_tests tests/SourceFormatEvidenceTests.cpp)
target_include_directories(source_format_tests PRIVATE include)
add_test(NAME source_format_parsing COMMAND source_format_tests)
add_executable(converter_evidence_tests tests/ConverterEvidenceTableTests.cpp)
target_include_directories(converter_evidence_tests PRIVATE include)
add_test(NAME converter_lifecycle COMMAND converter_evidence_tests)
add_test(NAME loader_rejects_unrelated_process COMMAND apple_source_probe_loader --validate-self)

# The fixture observer has separate filename and test-only entry points. It is
# never loadable through the production loader's fixed DLL path.
add_library(fixture_toolbox SHARED tests/FixtureAudioConverter.cpp)
target_include_directories(fixture_toolbox PRIVATE include)
set_target_properties(fixture_toolbox PROPERTIES OUTPUT_NAME "FixtureCoreAudioToolbox")
add_library(apple_probe_fixture SHARED src/ProbeDll.cpp)
target_include_directories(apple_probe_fixture PRIVATE include)
target_compile_definitions(apple_probe_fixture PRIVATE DX5_APPLE_PROBE_FIXTURE)
target_link_libraries(apple_probe_fixture PRIVATE probe_minhook advapi32)
set_target_properties(apple_probe_fixture PROPERTIES OUTPUT_NAME "dx5-apple-source-probe-fixture")
add_executable(hook_abi_fixture tests/HookAbiFixture.cpp)
target_include_directories(hook_abi_fixture PRIVATE include)
target_link_libraries(hook_abi_fixture PRIVATE advapi32)
add_dependencies(hook_abi_fixture fixture_toolbox apple_probe_fixture)
add_test(NAME native_hook_abi COMMAND hook_abi_fixture)

foreach(target apple_source_probe apple_source_probe_loader source_format_tests converter_evidence_tests fixture_toolbox apple_probe_fixture hook_abi_fixture)
  target_compile_definitions(${target} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
  if(MSVC)
    target_compile_options(${target} PRIVATE /W4 /WX /permissive- /EHsc /guard:cf /utf-8)
    target_link_options(${target} PRIVATE /guard:cf)
  endif()
endforeach()
