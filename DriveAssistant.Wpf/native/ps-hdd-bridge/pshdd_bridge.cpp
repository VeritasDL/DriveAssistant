#include <disk/disk.hpp>
#include <disk/disk_config.hpp>
#include <disk/partition.hpp>
#include <formats/disk_format_factory.hpp>
#include <io/stream/disk_stream_factory.hpp>
#include <vfs/directory.hpp>
#include <vfs/file.hpp>
#include <vfs/node.hpp>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef PSHDD_BRIDGE_EXPORTS
#define PSHDD_API extern "C" __declspec(dllexport)
#else
#define PSHDD_API extern "C" __declspec(dllimport)
#endif

using ProgressCallback = void(__cdecl*)(int percent, const char* stage, const char* message);

namespace {

constexpr int kSuccess = 0;
constexpr int kInvalidArgument = 1;
constexpr int kFailure = 2;
constexpr int kBufferTooSmall = 3;
constexpr uint64_t kDefaultEstimatedAllocationUnit = 0x1000;
ProgressCallback g_progressCallback = nullptr;

struct OffsetMetrics
{
  size_t dataOffsetCount = 0;
  size_t dataRunCount = 0;
  size_t largestRunBlocks = 0;
  uint64_t largestRunBytes = 0;
  uint64_t estimatedAllocationUnit = kDefaultEstimatedAllocationUnit;
  std::string status;
  std::string dataRanges;
};

struct FileWriteResult
{
  uint64_t bytesWritten = 0;
  bool recovered = false;
  std::string error;
};

struct RecoverySummary
{
  size_t totalNodes = 0;
  size_t totalFiles = 0;
  size_t recoveredFiles = 0;
  size_t skippedDirectories = 0;
  size_t skippedNoOffsets = 0;
  size_t failedFiles = 0;
  size_t contiguousFiles = 0;
  size_t fragmentedFiles = 0;
  size_t fullyFragmentedFiles = 0;
  uint64_t bytesWritten = 0;
  std::vector<std::string> failures;
};

void reportProgress(int percent, const std::string& stage, const std::string& message)
{
  if (g_progressCallback) {
    g_progressCallback(percent, stage.c_str(), message.c_str());
  }
}

std::string wideToUtf8(const wchar_t* value)
{
  if (!value || !*value) {
    return std::string();
  }

  const int needed = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
  if (needed <= 0) {
    throw std::runtime_error("Failed to convert UTF-16 path to UTF-8.");
  }

  std::string result(static_cast<size_t>(needed - 1), '\0');
  WideCharToMultiByte(CP_UTF8, 0, value, -1, &result[0], needed, nullptr, nullptr);
  return result;
}

std::vector<char> readAllBytes(const std::string& path)
{
  std::ifstream file(path, std::ios::binary);
  if (!file.is_open()) {
    throw std::runtime_error("Failed to open key file: " + path);
  }

  file.seekg(0, std::ios::end);
  const auto length = file.tellg();
  file.seekg(0, std::ios::beg);

  if (length <= 0) {
    throw std::runtime_error("Key file is empty: " + path);
  }

  std::vector<char> data(static_cast<size_t>(length));
  if (!file.read(data.data(), length)) {
    throw std::runtime_error("Failed to read key file: " + path);
  }
  return data;
}

std::unique_ptr<disk::Disk> openDisk(const std::string& imagePath, const std::string& keyPath)
{
  auto keyData = readAllBytes(keyPath);
  auto imagePathCopy = imagePath;

  disk::DiskConfig config;
  config.setKeys(keyData.data(), static_cast<uint32_t>(keyData.size()));
  config.setStream(io::stream::DiskStreamFactory::getStream(imagePathCopy));

  disk::Disk* rawDisk = formats::DiskFormatFactory::getInstance()->detectFormat(&config);
  if (!rawDisk) {
    throw std::runtime_error("Could not detect PlayStation disk format. Check the HDD image and key file.");
  }

  if (rawDisk->getPartitions().empty()) {
    delete rawDisk;
    throw std::runtime_error("Could not find any partitions in this PlayStation disk.");
  }

  return std::unique_ptr<disk::Disk>(rawDisk);
}

disk::Partition* findPartition(disk::Disk* disk, const std::string& partitionName)
{
  auto partitionNameCopy = partitionName;
  auto partition = disk->getPartitionByName(partitionNameCopy);
  if (!partition) {
    throw std::runtime_error("No PlayStation partition named: " + partitionName);
  }

  return partition;
}

std::vector<uint64_t> getOffsets(vfs::VfsNode* node, const char* key)
{
  std::string keyCopy(key);
  auto offsets = node->getOffsets(keyCopy);
  std::sort(offsets.begin(), offsets.end());
  offsets.erase(std::unique(offsets.begin(), offsets.end()), offsets.end());
  return offsets;
}

std::vector<uint64_t> getOrderedOffsets(vfs::VfsNode* node, const char* key)
{
  std::string keyCopy(key);
  return node->getOrderedOffsets(keyCopy);
}

bool directoryExists(const std::string& path)
{
  const auto attributes = GetFileAttributesA(path.c_str());
  return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

void ensureDirectory(const std::string& path)
{
  if (path.empty() || directoryExists(path)) {
    return;
  }

  std::string current;
  for (size_t index = 0; index < path.size(); ++index) {
    const auto ch = path[index];
    current.push_back(ch);
    const bool atSeparator = ch == '\\' || ch == '/';
    const bool atEnd = index == path.size() - 1;
    if (!atSeparator && !atEnd) {
      continue;
    }

    auto candidate = current;
    while (!candidate.empty() && (candidate.back() == '\\' || candidate.back() == '/')) {
      candidate.pop_back();
    }

    if (candidate.empty() || candidate.back() == ':') {
      continue;
    }

    if (!directoryExists(candidate) && !CreateDirectoryA(candidate.c_str(), nullptr)) {
      const auto error = GetLastError();
      if (error != ERROR_ALREADY_EXISTS) {
        throw std::runtime_error("Failed to create directory: " + candidate);
      }
    }
  }
}

void appendJsonEscaped(std::ostringstream& output, const std::string& value)
{
  output << '"';
  for (const auto ch : value) {
    switch (ch) {
      case '\\': output << "\\\\"; break;
      case '"': output << "\\\""; break;
      case '\b': output << "\\b"; break;
      case '\f': output << "\\f"; break;
      case '\n': output << "\\n"; break;
      case '\r': output << "\\r"; break;
      case '\t': output << "\\t"; break;
      default:
        if (static_cast<unsigned char>(ch) < 0x20) {
          output << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                 << static_cast<int>(static_cast<unsigned char>(ch))
                 << std::dec << std::setfill(' ');
        }
        else {
          output << ch;
        }
        break;
    }
  }
  output << '"';
}

std::string formatDateTime(vfs::VfsDateTime* value)
{
  if (!value || value->getYear() <= 0 || value->getMonth() <= 0 || value->getDay() <= 0) {
    return std::string();
  }

  std::ostringstream output;
  output << std::setfill('0')
         << std::setw(4) << value->getYear() << '-'
         << std::setw(2) << value->getMonth() << '-'
         << std::setw(2) << value->getDay() << ' '
         << std::setw(2) << value->getHour() << ':'
         << std::setw(2) << value->getMinute() << ':'
         << std::setw(2) << value->getSecond();
  return output.str();
}

std::string formatHex(uint64_t value)
{
  std::ostringstream output;
  output << "0x" << std::uppercase << std::hex << value;
  return output.str();
}

std::string formatOffsetArray(const std::vector<uint64_t>& offsets)
{
  std::ostringstream output;
  for (size_t index = 0; index < offsets.size(); ++index) {
    if (index > 0) {
      output << ", ";
    }
    output << formatHex(offsets[index]);
  }
  return output.str();
}

uint64_t estimateAllocationUnit(const std::vector<uint64_t>& offsets)
{
  uint64_t result = 0;
  for (size_t index = 1; index < offsets.size(); ++index) {
    const auto delta = offsets[index] - offsets[index - 1];
    if (delta == 0) {
      continue;
    }

    if (result == 0 || delta < result) {
      result = delta;
    }
  }

  return result == 0 ? kDefaultEstimatedAllocationUnit : result;
}

uint64_t estimateReadUnit(const std::vector<uint64_t>& sortedOffsets, uint64_t fileSize)
{
  auto unit = estimateAllocationUnit(sortedOffsets);
  if (!sortedOffsets.empty() && fileSize > 0) {
    const auto averageUnit = (fileSize + sortedOffsets.size() - 1) / sortedOffsets.size();
    if (averageUnit > unit) {
      unit = averageUnit;
    }
  }

  return unit == 0 ? kDefaultEstimatedAllocationUnit : unit;
}

OffsetMetrics calculateOffsetMetrics(const std::vector<uint64_t>& dataOffsets, bool isDirectory)
{
  OffsetMetrics metrics;
  metrics.dataOffsetCount = dataOffsets.size();
  metrics.estimatedAllocationUnit = estimateAllocationUnit(dataOffsets);

  if (isDirectory) {
    metrics.status = "Directory";
    return metrics;
  }

  if (dataOffsets.empty()) {
    metrics.status = "No data offsets";
    return metrics;
  }

  metrics.dataRunCount = 1;
  size_t currentRunBlocks = 1;
  std::ostringstream ranges;
  uint64_t rangeStart = dataOffsets[0];
  uint64_t previous = dataOffsets[0];

  for (size_t index = 1; index < dataOffsets.size(); ++index) {
    const auto offset = dataOffsets[index];
    if (offset == previous + metrics.estimatedAllocationUnit) {
      currentRunBlocks++;
    }
    else {
      if (ranges.tellp() > 0) {
        ranges << "; ";
      }
      ranges << formatHex(rangeStart);
      if (rangeStart != previous) {
        ranges << "-" << formatHex(previous);
      }

      metrics.largestRunBlocks = std::max(metrics.largestRunBlocks, currentRunBlocks);
      metrics.dataRunCount++;
      rangeStart = offset;
      currentRunBlocks = 1;
    }

    previous = offset;
  }

  if (ranges.tellp() > 0) {
    ranges << "; ";
  }
  ranges << formatHex(rangeStart);
  if (rangeStart != previous) {
    ranges << "-" << formatHex(previous);
  }

  metrics.largestRunBlocks = std::max(metrics.largestRunBlocks, currentRunBlocks);
  metrics.largestRunBytes = metrics.largestRunBlocks * metrics.estimatedAllocationUnit;
  metrics.dataRanges = ranges.str();

  if (metrics.dataRunCount <= 1) {
    metrics.status = "Contiguous";
  }
  else if (metrics.dataRunCount == metrics.dataOffsetCount) {
    metrics.status = "Fully fragmented";
  }
  else {
    metrics.status = "Fragmented";
  }

  return metrics;
}

void appendDirectory(std::ostringstream& output, vfs::VfsDirectory* directory, int level)
{
  for (auto child : directory->getChildren()) {
    for (int index = 0; index < level; index++) {
      output << "  ";
    }

    output << child->getName() << '\n';
    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      appendDirectory(output, static_cast<vfs::VfsDirectory*>(child), level + 1);
    }
  }
}

void appendJsonFileEntry(
  std::ostringstream& output,
  vfs::VfsNode* node,
  const std::string& path,
  bool& first)
{
  const bool isDirectory = node->getType() == vfs::VfsNodeType::DIRECTORY;
  const auto dataOffsets = getOffsets(node, "data");
  const auto inodeOffsets = getOffsets(node, "inode");
  const auto direntOffsets = getOffsets(node, "dirent");
  const auto blocktableOffsets = getOffsets(node, "blocktable");
  const auto metrics = calculateOffsetMetrics(dataOffsets, isDirectory);
  uint64_t fileSize = 0;
  if (!isDirectory) {
    fileSize = static_cast<vfs::VfsFile*>(node)->getFileSize();
  }

  if (!first) {
    output << ',';
  }
  first = false;

  output << '{';
  output << "\"path\":";
  appendJsonEscaped(output, path);
  output << ",\"name\":";
  appendJsonEscaped(output, node->getName());
  output << ",\"type\":\"" << (isDirectory ? "Directory" : "File") << '"';
  output << ",\"size\":" << fileSize;
  output << ",\"created\":";
  appendJsonEscaped(output, formatDateTime(node->getCreationTime()));
  output << ",\"modified\":";
  appendJsonEscaped(output, formatDateTime(node->getLastModifiedTime()));
  output << ",\"accessed\":";
  appendJsonEscaped(output, formatDateTime(node->getLastAccessTime()));
  output << ",\"dataOffsetCount\":" << metrics.dataOffsetCount;
  output << ",\"dataRunCount\":" << metrics.dataRunCount;
  output << ",\"largestRunBytes\":" << metrics.largestRunBytes;
  output << ",\"estimatedAllocationUnit\":" << metrics.estimatedAllocationUnit;
  output << ",\"fragmentationStatus\":";
  appendJsonEscaped(output, metrics.status);
  output << ",\"dataRanges\":";
  appendJsonEscaped(output, metrics.dataRanges);
  output << ",\"dataOffsets\":";
  appendJsonEscaped(output, formatOffsetArray(dataOffsets));
  output << ",\"inodeOffsets\":";
  appendJsonEscaped(output, formatOffsetArray(inodeOffsets));
  output << ",\"direntOffsets\":";
  appendJsonEscaped(output, formatOffsetArray(direntOffsets));
  output << ",\"blocktableOffsets\":";
  appendJsonEscaped(output, formatOffsetArray(blocktableOffsets));
  output << '}';
}

void appendJsonDirectory(
  std::ostringstream& output,
  vfs::VfsDirectory* directory,
  const std::string& parentPath,
  bool& first)
{
  for (auto child : directory->getChildren()) {
    const auto childPath = parentPath == "/"
      ? parentPath + child->getName()
      : parentPath + "/" + child->getName();

    appendJsonFileEntry(output, child, childPath, first);

    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      appendJsonDirectory(output, static_cast<vfs::VfsDirectory*>(child), childPath, first);
    }
  }
}

size_t countDirectoryNodes(vfs::VfsDirectory* directory)
{
  size_t count = 0;
  for (auto child : directory->getChildren()) {
    count++;
    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      count += countDirectoryNodes(static_cast<vfs::VfsDirectory*>(child));
    }
  }

  return count;
}

void appendJsonDirectoryWithProgress(
  std::ostringstream& output,
  vfs::VfsDirectory* directory,
  const std::string& parentPath,
  bool& first,
  size_t totalNodes,
  size_t& processedNodes)
{
  for (auto child : directory->getChildren()) {
    const auto childPath = parentPath == "/"
      ? parentPath + child->getName()
      : parentPath + "/" + child->getName();

    appendJsonFileEntry(output, child, childPath, first);
    processedNodes++;
    if (processedNodes == 1 || processedNodes % 25 == 0 || processedNodes == totalNodes) {
      const auto percent = totalNodes == 0
        ? 95
        : 60 + static_cast<int>((processedNodes * 35) / totalNodes);
      reportProgress(percent, "Indexing files", childPath);
    }

    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      appendJsonDirectoryWithProgress(
        output,
        static_cast<vfs::VfsDirectory*>(child),
        childPath,
        first,
        totalNodes,
        processedNodes);
    }
  }
}

std::string normalizeVfsPath(const std::string& path)
{
  if (path.empty() || path == "/") {
    return "/";
  }

  auto normalized = path;
  std::replace(normalized.begin(), normalized.end(), '\\', '/');
  if (normalized.front() != '/') {
    normalized.insert(normalized.begin(), '/');
  }

  while (normalized.size() > 1 && normalized.back() == '/') {
    normalized.pop_back();
  }

  return normalized;
}

std::string sanitizePathSegment(const std::string& value)
{
  std::string result;
  for (const auto ch : value) {
    switch (ch) {
      case '<':
      case '>':
      case ':':
      case '"':
      case '/':
      case '\\':
      case '|':
      case '?':
      case '*':
        result.push_back('_');
        break;
      default:
        if (static_cast<unsigned char>(ch) < 0x20) {
          result.push_back('_');
        }
        else {
          result.push_back(ch);
        }
        break;
    }
  }

  while (!result.empty() && (result.back() == '.' || result.back() == ' ')) {
    result.pop_back();
  }

  return result.empty() ? "_" : result;
}

std::string pathDirectoryName(const std::string& path)
{
  const auto slash = path.find_last_of("\\/");
  return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

std::string makeOutputPath(const std::string& outputRoot, const std::string& partitionName, const std::string& vfsPath)
{
  auto result = outputRoot;
  while (!result.empty() && (result.back() == '\\' || result.back() == '/')) {
    result.pop_back();
  }

  result += "\\";
  result += sanitizePathSegment(partitionName);

  const auto normalized = normalizeVfsPath(vfsPath);
  size_t start = 1;
  while (start < normalized.size()) {
    const auto slash = normalized.find('/', start);
    const auto length = slash == std::string::npos ? std::string::npos : slash - start;
    result += "\\";
    result += sanitizePathSegment(normalized.substr(start, length));

    if (slash == std::string::npos) {
      break;
    }
    start = slash + 1;
  }

  return result;
}

vfs::VfsNode* findNodeByPath(vfs::VfsDirectory* directory, const std::string& targetPath, const std::string& parentPath)
{
  for (auto child : directory->getChildren()) {
    const auto childPath = parentPath == "/"
      ? parentPath + child->getName()
      : parentPath + "/" + child->getName();

    if (childPath == targetPath) {
      return child;
    }

    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      auto match = findNodeByPath(static_cast<vfs::VfsDirectory*>(child), targetPath, childPath);
      if (match) {
        return match;
      }
    }
  }

  return nullptr;
}

std::string listPartitions(const std::string& imagePath, const std::string& keyPath)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  reportProgress(75, "Reading partition table", "Listing PlayStation partitions");
  std::ostringstream output;

  output << std::setw(26) << std::left << "Partition Name"
         << std::setw(16) << std::left << "Start"
         << std::setw(16) << std::left << "End"
         << std::setw(16) << std::left << "Length"
         << '\n';

  for (auto partition : disk->getPartitions()) {
    output << std::setw(26) << std::left << partition->getName()
           << std::setw(16) << std::left << std::hex << partition->getStart()
           << std::setw(16) << std::left << std::hex << partition->getEnd()
           << std::setw(16) << std::left << std::hex << partition->getLength()
           << '\n';
  }

  reportProgress(100, "Complete", "Partition list loaded");
  return output.str();
}

std::string displayPartition(const std::string& imagePath, const std::string& keyPath, const std::string& partitionName)
{
  auto disk = openDisk(imagePath, keyPath);
  auto partition = findPartition(disk.get(), partitionName);

  partition->mount();
  auto vfs = partition->getVfs();
  if (!vfs) {
    throw std::runtime_error("Could not create PlayStation partition file system: " + partitionName);
  }

  if (!vfs->isMounted()) {
    throw std::runtime_error("Could not mount PlayStation partition file system: " + partitionName);
  }

  std::ostringstream output;
  appendDirectory(output, vfs->getRoot(), 0);
  return output.str();
}

std::string listFilesJson(const std::string& imagePath, const std::string& keyPath, const std::string& partitionName)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  reportProgress(15, "Detecting format", "PlayStation disk format detected");
  auto partition = findPartition(disk.get(), partitionName);

  reportProgress(-1, "Mounting file system", partitionName);
  partition->mount();
  auto vfs = partition->getVfs();
  if (!vfs) {
    throw std::runtime_error("Could not create PlayStation partition file system: " + partitionName);
  }

  if (!vfs->isMounted()) {
    throw std::runtime_error("Could not mount PlayStation partition file system: " + partitionName);
  }
  reportProgress(60, "Indexing files", "Counting file system entries");

  std::ostringstream output;
  output << "{\"partition\":";
  appendJsonEscaped(output, partitionName);
  output << ",\"estimatedAllocationUnit\":" << kDefaultEstimatedAllocationUnit;
  output << ",\"files\":[";

  bool first = true;
  const auto totalNodes = countDirectoryNodes(vfs->getRoot());
  size_t processedNodes = 0;
  appendJsonDirectoryWithProgress(output, vfs->getRoot(), "/", first, totalNodes, processedNodes);

  output << "]}";
  reportProgress(100, "Complete", "PlayStation file index loaded");
  return output.str();
}

std::string decryptPartition(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& outputPath)
{
  if (outputPath.empty()) {
    throw std::runtime_error("Output path is required.");
  }

  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  auto partition = findPartition(disk.get(), partitionName);
  reportProgress(10, "Exporting partition", partitionName);

  std::ofstream file(outputPath, std::ios::binary | std::ios::trunc);
  if (!file.is_open()) {
    throw std::runtime_error("Failed to open output file: " + outputPath);
  }

  constexpr uint32_t bufferSize = 0x200 * 0x800;
  std::vector<char> buffer(bufferSize);
  auto dataProvider = partition->getDataProvider();
  auto remaining = partition->getLength();
  uint64_t written = 0;

  dataProvider->seek(0);
  while (remaining > 0) {
    const auto request = static_cast<uint32_t>(std::min<uint64_t>(buffer.size(), remaining));
    const auto readLength = dataProvider->read(buffer.data(), request);
    if (readLength == 0) {
      throw std::runtime_error("Unexpected end of partition while decrypting: " + partitionName);
    }

    file.write(buffer.data(), static_cast<std::streamsize>(readLength));
    if (!file.good()) {
      throw std::runtime_error("Failed to write decrypted partition output: " + outputPath);
    }

    remaining -= readLength;
    written += readLength;
    const auto percent = partition->getLength() == 0
      ? 100
      : 10 + static_cast<int>((written * 90) / partition->getLength());
    reportProgress(percent, "Exporting partition", partitionName);
  }

  std::ostringstream message;
  message << "Decrypted partition " << partitionName << " to " << outputPath
          << " (" << written << " bytes).";
  reportProgress(100, "Complete", message.str());
  return message.str();
}

FileWriteResult writeFileNode(disk::Partition* partition, vfs::VfsNode* node, const std::string& outputPath)
{
  if (node->getType() == vfs::VfsNodeType::DIRECTORY) {
    throw std::runtime_error("Cannot export a directory as a file.");
  }

  auto file = static_cast<vfs::VfsFile*>(node);
  auto remaining = file->getFileSize();
  auto orderedOffsets = getOrderedOffsets(node, "data");
  auto sortedOffsets = orderedOffsets;
  std::sort(sortedOffsets.begin(), sortedOffsets.end());
  sortedOffsets.erase(std::unique(sortedOffsets.begin(), sortedOffsets.end()), sortedOffsets.end());
  const auto unit = estimateReadUnit(sortedOffsets, remaining);

  if (remaining > 0 && orderedOffsets.empty()) {
    throw std::runtime_error("Selected file has no recoverable data offsets.");
  }

  ensureDirectory(pathDirectoryName(outputPath));
  std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
  if (!output.is_open()) {
    throw std::runtime_error("Failed to open output file: " + outputPath);
  }

  constexpr uint64_t maxBufferSize = 1024 * 1024;
  std::vector<char> buffer(static_cast<size_t>(std::min<uint64_t>(unit, maxBufferSize)));
  auto dataProvider = partition->getDataProvider();
  uint64_t written = 0;

  try {
    for (const auto offset : orderedOffsets) {
      if (remaining == 0) {
        break;
      }

      auto bytesFromOffset = std::min<uint64_t>(unit, remaining);
      uint64_t consumedFromOffset = 0;
      while (bytesFromOffset > 0) {
        const auto request = static_cast<uint32_t>(std::min<uint64_t>(buffer.size(), bytesFromOffset));
        dataProvider->seek(static_cast<int64_t>(offset + consumedFromOffset));
        const auto readLength = dataProvider->read(buffer.data(), request);
        if (readLength == 0) {
          throw std::runtime_error("Unexpected end of file data while exporting.");
        }

        output.write(buffer.data(), static_cast<std::streamsize>(readLength));
        if (!output.good()) {
          throw std::runtime_error("Failed to write exported file: " + outputPath);
        }

        remaining -= readLength;
        written += readLength;
        consumedFromOffset += readLength;
        bytesFromOffset -= readLength;
      }
    }

    if (remaining > 0) {
      throw std::runtime_error("Not enough data offsets to fully export file.");
    }
  }
  catch (...) {
    output.close();
    DeleteFileA(outputPath.c_str());
    throw;
  }

  FileWriteResult result;
  result.bytesWritten = written;
  result.recovered = true;
  return result;
}

std::string exportFile(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& filePath,
  const std::string& outputPath)
{
  if (filePath.empty() || outputPath.empty()) {
    throw std::runtime_error("File path and output path are required.");
  }

  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  auto partition = findPartition(disk.get(), partitionName);

  reportProgress(-1, "Mounting file system", partitionName);
  partition->mount();
  auto vfs = partition->getVfs();
  if (!vfs || !vfs->isMounted()) {
    throw std::runtime_error("Could not mount PlayStation partition file system: " + partitionName);
  }

  auto targetPath = normalizeVfsPath(filePath);
  auto node = findNodeByPath(vfs->getRoot(), targetPath, "/");
  if (!node) {
    throw std::runtime_error("No PlayStation file named: " + targetPath);
  }

  reportProgress(50, "Exporting file", targetPath);
  auto result = writeFileNode(partition, node, outputPath);
  std::ostringstream message;
  message << "Exported " << targetPath << " to " << outputPath
          << " (" << result.bytesWritten << " bytes).";
  reportProgress(100, "Complete", message.str());
  return message.str();
}

void recoverDirectory(
  disk::Partition* partition,
  vfs::VfsDirectory* directory,
  const std::string& partitionName,
  const std::string& parentPath,
  const std::string& outputRoot,
  RecoverySummary& summary,
  size_t totalNodes)
{
  for (auto child : directory->getChildren()) {
    const auto childPath = parentPath == "/"
      ? parentPath + child->getName()
      : parentPath + "/" + child->getName();
    summary.totalNodes++;
    if (summary.totalNodes == 1 || summary.totalNodes % 10 == 0 || summary.totalNodes == totalNodes) {
      const auto percent = totalNodes == 0
        ? 95
        : 35 + static_cast<int>((summary.totalNodes * 60) / totalNodes);
      reportProgress(percent, "Recovering files", childPath);
    }

    if (child->getType() == vfs::VfsNodeType::DIRECTORY) {
      summary.skippedDirectories++;
      ensureDirectory(makeOutputPath(outputRoot, partitionName, childPath));
      recoverDirectory(partition, static_cast<vfs::VfsDirectory*>(child), partitionName, childPath, outputRoot, summary, totalNodes);
      continue;
    }

    summary.totalFiles++;
    const auto sortedOffsets = getOffsets(child, "data");
    const auto metrics = calculateOffsetMetrics(sortedOffsets, false);
    if (metrics.status == "Contiguous") {
      summary.contiguousFiles++;
    }
    else if (metrics.status == "Fragmented") {
      summary.fragmentedFiles++;
    }
    else if (metrics.status == "Fully fragmented") {
      summary.fullyFragmentedFiles++;
    }

    const auto file = static_cast<vfs::VfsFile*>(child);
    if (file->getFileSize() > 0 && sortedOffsets.empty()) {
      summary.skippedNoOffsets++;
      continue;
    }

    try {
      const auto outputPath = makeOutputPath(outputRoot, partitionName, childPath);
      auto result = writeFileNode(partition, child, outputPath);
      summary.recoveredFiles++;
      summary.bytesWritten += result.bytesWritten;
    }
    catch (const std::exception& ex) {
      summary.failedFiles++;
      if (summary.failures.size() < 50) {
        summary.failures.push_back(childPath + ": " + ex.what());
      }
    }
  }
}

std::string recoverFilesJson(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& outputRoot)
{
  if (outputRoot.empty()) {
    throw std::runtime_error("Output directory is required.");
  }

  reportProgress(0, "Opening image", imagePath);
  ensureDirectory(outputRoot);
  auto disk = openDisk(imagePath, keyPath);
  auto partition = findPartition(disk.get(), partitionName);
  reportProgress(-1, "Mounting file system", partitionName);
  partition->mount();
  auto vfs = partition->getVfs();
  if (!vfs || !vfs->isMounted()) {
    throw std::runtime_error("Could not mount PlayStation partition file system: " + partitionName);
  }

  RecoverySummary summary;
  ensureDirectory(makeOutputPath(outputRoot, partitionName, "/"));
  reportProgress(30, "Preparing recovery", "Counting file system entries");
  const auto totalNodes = countDirectoryNodes(vfs->getRoot());
  recoverDirectory(partition, vfs->getRoot(), partitionName, "/", outputRoot, summary, totalNodes);

  std::ostringstream output;
  output << '{';
  output << "\"partition\":";
  appendJsonEscaped(output, partitionName);
  output << ",\"outputRoot\":";
  appendJsonEscaped(output, outputRoot);
  output << ",\"totalNodes\":" << summary.totalNodes;
  output << ",\"totalFiles\":" << summary.totalFiles;
  output << ",\"recoveredFiles\":" << summary.recoveredFiles;
  output << ",\"skippedDirectories\":" << summary.skippedDirectories;
  output << ",\"skippedNoOffsets\":" << summary.skippedNoOffsets;
  output << ",\"failedFiles\":" << summary.failedFiles;
  output << ",\"contiguousFiles\":" << summary.contiguousFiles;
  output << ",\"fragmentedFiles\":" << summary.fragmentedFiles;
  output << ",\"fullyFragmentedFiles\":" << summary.fullyFragmentedFiles;
  output << ",\"bytesWritten\":" << summary.bytesWritten;
  output << ",\"failures\":[";
  for (size_t index = 0; index < summary.failures.size(); ++index) {
    if (index > 0) {
      output << ',';
    }
    appendJsonEscaped(output, summary.failures[index]);
  }
  output << "]}";
  reportProgress(100, "Complete", "PlayStation recovery complete");
  return output.str();
}

int copyString(const std::string& value, char* buffer, int bufferLength, int* requiredBytes)
{
  const int needed = static_cast<int>(value.size()) + 1;
  if (requiredBytes) {
    *requiredBytes = needed;
  }

  if (!buffer || bufferLength <= 0) {
    return kBufferTooSmall;
  }

  if (bufferLength < needed) {
    const int copyLength = bufferLength - 1;
    if (copyLength > 0) {
      memcpy(buffer, value.data(), static_cast<size_t>(copyLength));
      buffer[copyLength] = '\0';
    }
    return kBufferTooSmall;
  }

  memcpy(buffer, value.c_str(), static_cast<size_t>(needed));
  return kSuccess;
}

int runBridge(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes,
  bool display)
{
  try {
    const auto image = wideToUtf8(imagePath);
    const auto key = wideToUtf8(keyPath);
    if (image.empty() || key.empty()) {
      return copyString("Image path and key path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    const auto text = display
      ? displayPartition(image, key, wideToUtf8(partitionName))
      : listPartitions(image, key);

    return copyString(text, output, outputLength, requiredBytes);
  }
  catch (const std::exception& ex) {
    copyString(ex.what(), output, outputLength, requiredBytes);
    return kFailure;
  }
  catch (...) {
    copyString("Unknown PS HDD bridge failure.", output, outputLength, requiredBytes);
    return kFailure;
  }
}

} // namespace

PSHDD_API void pshdd_set_progress_callback(ProgressCallback callback)
{
  g_progressCallback = callback;
}

PSHDD_API int pshdd_list_partitions(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  return runBridge(imagePath, keyPath, nullptr, output, outputLength, requiredBytes, false);
}

PSHDD_API int pshdd_display_partition(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  return runBridge(imagePath, keyPath, partitionName, output, outputLength, requiredBytes, true);
}

PSHDD_API int pshdd_list_files_json(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto image = wideToUtf8(imagePath);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    if (image.empty() || key.empty() || partition.empty()) {
      return copyString("Image path, key path, and partition name are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    return copyString(listFilesJson(image, key, partition), output, outputLength, requiredBytes);
  }
  catch (const std::exception& ex) {
    copyString(ex.what(), output, outputLength, requiredBytes);
    return kFailure;
  }
  catch (...) {
    copyString("Unknown PS HDD bridge failure.", output, outputLength, requiredBytes);
    return kFailure;
  }
}

PSHDD_API int pshdd_decrypt_partition(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  const wchar_t* outputPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto image = wideToUtf8(imagePath);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto destination = wideToUtf8(outputPath);
    if (image.empty() || key.empty() || partition.empty() || destination.empty()) {
      return copyString("Image path, key path, partition name, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    return copyString(decryptPartition(image, key, partition, destination), output, outputLength, requiredBytes);
  }
  catch (const std::exception& ex) {
    copyString(ex.what(), output, outputLength, requiredBytes);
    return kFailure;
  }
  catch (...) {
    copyString("Unknown PS HDD bridge failure.", output, outputLength, requiredBytes);
    return kFailure;
  }
}

PSHDD_API int pshdd_export_file(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  const wchar_t* filePath,
  const wchar_t* outputPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto image = wideToUtf8(imagePath);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto source = wideToUtf8(filePath);
    const auto destination = wideToUtf8(outputPath);
    if (image.empty() || key.empty() || partition.empty() || source.empty() || destination.empty()) {
      return copyString("Image path, key path, partition name, file path, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    return copyString(exportFile(image, key, partition, source, destination), output, outputLength, requiredBytes);
  }
  catch (const std::exception& ex) {
    copyString(ex.what(), output, outputLength, requiredBytes);
    return kFailure;
  }
  catch (...) {
    copyString("Unknown PS HDD bridge failure.", output, outputLength, requiredBytes);
    return kFailure;
  }
}

PSHDD_API int pshdd_recover_files_json(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  const wchar_t* outputDirectory,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto image = wideToUtf8(imagePath);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto destination = wideToUtf8(outputDirectory);
    if (image.empty() || key.empty() || partition.empty() || destination.empty()) {
      return copyString("Image path, key path, partition name, and output directory are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    return copyString(recoverFilesJson(image, key, partition, destination), output, outputLength, requiredBytes);
  }
  catch (const std::exception& ex) {
    copyString(ex.what(), output, outputLength, requiredBytes);
    return kFailure;
  }
  catch (...) {
    copyString("Unknown PS HDD bridge failure.", output, outputLength, requiredBytes);
    return kFailure;
  }
}

PSHDD_API int pshdd_bridge_version()
{
  return 5;
}
