
macro(append_extra_system_libs NativeLibsExtra)
    if (CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_ANDROID)
        list(APPEND ${NativeLibsExtra} rt)
    elseif (CLR_CMAKE_TARGET_FREEBSD)
        list(APPEND ${NativeLibsExtra} pthread)
        find_library(INOTIFY_LIBRARY inotify HINTS /usr/local/lib)
        list(APPEND ${NativeLibsExtra} ${INOTIFY_LIBRARY})
    elseif (CLR_CMAKE_TARGET_SUNOS)
        list(APPEND ${NativeLibsExtra} socket)
    endif ()

    if (CLR_CMAKE_TARGET_OSX OR CLR_CMAKE_TARGET_MACCATALYST OR  CLR_CMAKE_TARGET_IOS OR CLR_CMAKE_TARGET_TVOS)
        include(CMakeFindFrameworks)
        find_library(FOUNDATION Foundation REQUIRED)
        list(APPEND ${NativeLibsExtra} ${FOUNDATION})
    endif ()
endmacro()