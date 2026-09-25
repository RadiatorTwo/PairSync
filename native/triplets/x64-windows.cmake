set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
# build-win.ps1 pre-seeds LIB/INCLUDE with the Universal CRT paths when the Windows SDK
# registry entry (KitsRoot10) is missing; vcvarsall appends to these instead of replacing them.
set(VCPKG_ENV_PASSTHROUGH_UNTRACKED LIB INCLUDE)
