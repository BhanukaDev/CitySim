#!/bin/sh
# Builds the erosion library into bin/ (committed, so the project runs without a compile step).
# Run again after changing erosion.cpp. macOS: universal (arm64 + x86_64). Linux/Windows lines are for later.
set -e
cd "$(dirname "$0")"
mkdir -p bin
case "$(uname -s)" in
  Darwin)
    clang++ -O3 -std=c++20 -shared -fPIC -fvisibility=hidden -arch arm64 -arch x86_64 \
      -mmacosx-version-min=11.0 -o bin/libcitysim_erosion.dylib erosion.cpp ;;
  Linux)
    g++ -O3 -std=c++20 -shared -fPIC -fvisibility=hidden -pthread -o bin/libcitysim_erosion.so erosion.cpp ;;
  *)
    echo "Windows: cl /O2 /std:c++20 /LD erosion.cpp /Fe:bin\\citysim_erosion.dll" >&2; exit 1 ;;
esac
echo "Built $(ls bin/*citysim_erosion*)"
