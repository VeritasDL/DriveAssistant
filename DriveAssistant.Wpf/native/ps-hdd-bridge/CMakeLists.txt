cmake_minimum_required(VERSION 3.16)
project(PSHddBridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED PS_HDD_TOOLS_ROOT)
  get_filename_component(DEFAULT_PS_HDD_TOOLS_ROOT
    "${CMAKE_CURRENT_LIST_DIR}/../../../../ps-hdd-tools-src"
    ABSOLUTE)
  set(PS_HDD_TOOLS_ROOT "${DEFAULT_PS_HDD_TOOLS_ROOT}" CACHE PATH "Path to PS-HDD-Tools source root")
endif()

file(GLOB CORE_SOURCES
  "${PS_HDD_TOOLS_ROOT}/core/disk/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/crypto/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/crypto/aes/*.c"
  "${PS_HDD_TOOLS_ROOT}/core/formats/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/io/data/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/io/stream/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/logging/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/vfs/*.cpp"
  "${PS_HDD_TOOLS_ROOT}/core/vfs/adapters/*.cpp"
)

add_library(pshdd_bridge SHARED pshdd_bridge.cpp ${CORE_SOURCES})
target_include_directories(pshdd_bridge PRIVATE "${PS_HDD_TOOLS_ROOT}/core")
target_compile_definitions(pshdd_bridge PRIVATE PSHDD_BRIDGE_EXPORTS)

set_target_properties(pshdd_bridge PROPERTIES
  OUTPUT_NAME "pshdd-bridge"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

