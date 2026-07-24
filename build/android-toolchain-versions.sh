#!/usr/bin/env bash
# Pinned, auditable inputs for the project-local Android toolchain.

DOTNET_SDK_VERSION="9.0.118"
DOTNET_SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/9.0.118/dotnet-sdk-9.0.118-linux-x64.tar.gz"
DOTNET_SDK_SHA512="1c7bba718463cc4f8d162cb88808eba14ee1e72e26c84b5f3751c330c62a4a31af5f98afd5deeb5c272039b5dc649255cc89af201f8d7069bc021fab49d184c5"

JDK_VERSION="17.0.16+8"
JDK_ARCHIVE_NAME="OpenJDK17U-jdk_x64_linux_hotspot_17.0.16_8.tar.gz"
JDK_URL="https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.16%2B8/${JDK_ARCHIVE_NAME}"
JDK_SHA256="166774efcf0f722f2ee18eba0039de2d685b350ee14d7b69e6f83437dafd2af1"

ANDROID_COMMAND_LINE_TOOLS_VERSION="19.0"
ANDROID_COMMAND_LINE_TOOLS_ARCHIVE="commandlinetools-linux-13114758_latest.zip"
ANDROID_COMMAND_LINE_TOOLS_URL="https://dl.google.com/android/repository/${ANDROID_COMMAND_LINE_TOOLS_ARCHIVE}"
# Google repository2-1.xml publishes SHA-1 for this archive.
ANDROID_COMMAND_LINE_TOOLS_SHA1="5fdcc763663eefb86a5b8879697aa6088b041e70"

ANDROID_PLATFORM_VERSION="35"
ANDROID_BUILD_TOOLS_VERSION="35.0.0"
