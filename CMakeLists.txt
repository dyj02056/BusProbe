cmake_minimum_required(VERSION 3.20)
project(BusProbe CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(BUSPROBE_TESTS "단위 테스트 빌드 (GoogleTest를 내려받음)" ON)

find_package(Threads REQUIRED)

# 코덱과 시뮬레이터 코어 (정적 라이브러리)
add_library(bpcore STATIC core/can.cpp core/m1553.cpp core/sim.cpp)
target_include_directories(bpcore PUBLIC core)
target_link_libraries(bpcore PUBLIC Threads::Threads)

# TCP 시뮬레이터 실행 파일
add_executable(busprobe-simd simd/main.cpp)
target_link_libraries(busprobe-simd bpcore)
if(WIN32)
  target_link_libraries(busprobe-simd ws2_32)
else()
  # 컨테이너 런타임 이미지에 libstdc++ 버전이 달라도 실행되게 정적 링크
  target_link_options(busprobe-simd PRIVATE -static-libstdc++ -static-libgcc)
endif()

# 단위 테스트 (GoogleTest)
if(BUSPROBE_TESTS)
  include(FetchContent)
  FetchContent_Declare(googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG v1.15.2
    GIT_SHALLOW TRUE)
  set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
  set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
  FetchContent_MakeAvailable(googletest)

  enable_testing()
  add_executable(test_core core/tests/test_can.cpp core/tests/test_m1553.cpp core/tests/test_sim.cpp)
  target_link_libraries(test_core bpcore GTest::gtest_main)
  add_test(NAME core COMMAND test_core)
endif()
