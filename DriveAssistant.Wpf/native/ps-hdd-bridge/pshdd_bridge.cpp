#include <disk/disk.hpp>
#include <disk/disk_config.hpp>
#include <disk/partition.hpp>
#include <formats/disk_format_factory.hpp>
#include <io/stream/disk_stream_factory.hpp>
#include <vfs/directory.hpp>
#include <vfs/file.hpp>
#include <vfs/node.hpp>
#include <vfs/adapters/ufs/types.h>
#include <vfs/adapters/ufs/ffs/fs.h>
#include <vfs/adapters/ufs/ufs/dinode.h>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
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
#include <unordered_set>
#include <vector>

#ifdef PSHDD_BRIDGE_EXPORTS
#define PSHDD_API extern "C" __declspec(dllexport)
#else
#define PSHDD_API extern "C" __declspec(dllimport)
#endif

using ProgressCallback = void(__cdecl*)(int percent, const char* stage, const char* message);
using ReadCallback = uint64_t(__cdecl*)(void* context, uint64_t offset, char* data, uint32_t length);
using LengthCallback = uint64_t(__cdecl*)(void* context);

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

struct UfsDataExtent
{
  uint64_t offset = 0;
  uint64_t length = 0;
};

struct UfsOrphanInode
{
  uint32_t inodeNumber = 0;
  uint16_t mode = 0;
  int16_t linkCount = 0;
  uint64_t size = 0;
  uint64_t blocks = 0;
  int64_t created = 0;
  int64_t modified = 0;
  int64_t accessed = 0;
  uint64_t inodeOffset = 0;
  uint64_t dataOffsetCount = 0;
  uint64_t dataRunCount = 0;
  uint64_t largestRunBytes = 0;
  uint64_t estimatedAllocationUnit = kDefaultEstimatedAllocationUnit;
  std::string dataRanges;
  std::vector<UfsDataExtent> extents;
};

OffsetMetrics calculateOffsetMetrics(const std::vector<uint64_t>& dataOffsets, bool isDirectory);

class CallbackDiskStream final : public io::stream::DiskStream
{
public:
  CallbackDiskStream(void* context, ReadCallback readCallback, LengthCallback lengthCallback)
    : context(context), readCallback(readCallback), lengthCallback(lengthCallback), position(0)
  {
  }

  uint64_t read(char* data, uint32_t length) override
  {
    if (!readCallback || length == 0) {
      return 0;
    }

    const auto readLength = readCallback(context, position, data, length);
    position += readLength;
    return readLength;
  }

  uint64_t seek(int64_t offset, uint32_t whence = 0) override
  {
    int64_t next = 0;
    if (whence == 1) {
      next = static_cast<int64_t>(position) + offset;
    }
    else if (whence == 2) {
      next = static_cast<int64_t>(getLength()) + offset;
    }
    else {
      next = offset;
    }

    position = next <= 0 ? 0 : static_cast<uint64_t>(next);
    return position;
  }

  uint64_t tell() override
  {
    return position;
  }

  uint64_t getLength() const override
  {
    return lengthCallback ? lengthCallback(context) : 0;
  }

private:
  void* context;
  ReadCallback readCallback;
  LengthCallback lengthCallback;
  uint64_t position;
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

std::unique_ptr<disk::Disk> openDiskFromStream(
  const std::string& label,
  const std::string& keyPath,
  io::stream::DiskStream* stream)
{
  auto keyData = readAllBytes(keyPath);

  disk::DiskConfig config;
  config.setKeys(keyData.data(), static_cast<uint32_t>(keyData.size()));
  config.setStream(stream);

  disk::Disk* rawDisk = formats::DiskFormatFactory::getInstance()->detectFormat(&config);
  if (!rawDisk) {
    throw std::runtime_error("Could not detect PlayStation disk format from virtual image stream. Check the HDD image and key file: " + label);
  }

  if (rawDisk->getPartitions().empty()) {
    delete rawDisk;
    throw std::runtime_error("Could not find any partitions in this PlayStation disk: " + label);
  }

  return std::unique_ptr<disk::Disk>(rawDisk);
}

std::unique_ptr<disk::Disk> openDiskFromCallbacks(
  const std::string& label,
  const std::string& keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback)
{
  if (!context || !readCallback || !lengthCallback) {
    throw std::runtime_error("Virtual disk callbacks are required.");
  }

  return openDiskFromStream(
    label,
    keyPath,
    new CallbackDiskStream(context, readCallback, lengthCallback));
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

std::string listPartitionsFromDisk(disk::Disk* disk)
{
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

  return output.str();
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

uint16_t swap16Value(uint16_t value)
{
  return static_cast<uint16_t>((value >> 8) | (value << 8));
}

uint32_t swap32Value(uint32_t value)
{
  return ((value & 0x000000FFu) << 24) |
         ((value & 0x0000FF00u) << 8) |
         ((value & 0x00FF0000u) >> 8) |
         ((value & 0xFF000000u) >> 24);
}

uint64_t swap64Value(uint64_t value)
{
  return (static_cast<uint64_t>(swap32Value(static_cast<uint32_t>(value))) << 32) |
         swap32Value(static_cast<uint32_t>(value >> 32));
}

int16_t maybeSwapInt16(int16_t value, bool swap)
{
  return swap ? static_cast<int16_t>(swap16Value(static_cast<uint16_t>(value))) : value;
}

int32_t maybeSwapInt32(int32_t value, bool swap)
{
  return swap ? static_cast<int32_t>(swap32Value(static_cast<uint32_t>(value))) : value;
}

int64_t maybeSwapInt64(int64_t value, bool swap)
{
  return swap ? static_cast<int64_t>(swap64Value(static_cast<uint64_t>(value))) : value;
}

uint16_t maybeSwapUInt16(uint16_t value, bool swap)
{
  return swap ? swap16Value(value) : value;
}

uint32_t maybeSwapUInt32(uint32_t value, bool swap)
{
  return swap ? swap32Value(value) : value;
}

uint64_t maybeSwapUInt64(uint64_t value, bool swap)
{
  return swap ? swap64Value(value) : value;
}

void normalizeUfsSuperblock(fs& super, bool swap)
{
  if (!swap) {
    return;
  }

  super.fs_ncg = maybeSwapUInt32(super.fs_ncg, true);
  super.fs_bsize = maybeSwapInt32(super.fs_bsize, true);
  super.fs_fsize = maybeSwapInt32(super.fs_fsize, true);
  super.fs_frag = maybeSwapInt32(super.fs_frag, true);
  super.fs_fragshift = maybeSwapInt32(super.fs_fragshift, true);
  super.fs_fsbtodb = maybeSwapInt32(super.fs_fsbtodb, true);
  super.fs_nindir = maybeSwapInt32(super.fs_nindir, true);
  super.fs_inopb = maybeSwapUInt32(super.fs_inopb, true);
  super.fs_ipg = maybeSwapUInt32(super.fs_ipg, true);
  super.fs_fpg = maybeSwapInt32(super.fs_fpg, true);
  super.fs_iblkno = maybeSwapInt32(super.fs_iblkno, true);
  super.fs_magic = maybeSwapInt32(super.fs_magic, true);
}

ufs2_dinode normalizeUfsInode(const ufs2_dinode& source, bool swap)
{
  auto inode = source;
  if (!swap) {
    return inode;
  }

  inode.di_mode = maybeSwapUInt16(inode.di_mode, true);
  inode.di_nlink = maybeSwapInt16(inode.di_nlink, true);
  inode.di_size = maybeSwapUInt64(inode.di_size, true);
  inode.di_blocks = maybeSwapUInt64(inode.di_blocks, true);
  inode.di_atime = maybeSwapInt64(inode.di_atime, true);
  inode.di_mtime = maybeSwapInt64(inode.di_mtime, true);
  inode.di_ctime = maybeSwapInt64(inode.di_ctime, true);
  inode.di_birthtime = maybeSwapInt64(inode.di_birthtime, true);
  for (auto& block : inode.di_db) {
    block = maybeSwapInt64(block, true);
  }
  for (auto& block : inode.di_ib) {
    block = maybeSwapInt64(block, true);
  }
  return inode;
}

bool readProviderExact(io::data::DataProvider* provider, uint64_t offset, void* data, uint32_t length)
{
  provider->seek(static_cast<int64_t>(offset));
  return provider->read(static_cast<char*>(data), length) == length;
}

uint64_t inodeOffsetFor(const fs& super, uint32_t inodeNumber)
{
  const auto group = inodeNumber / super.fs_ipg;
  const auto groupInode = inodeNumber % super.fs_ipg;
  const auto inodeBlock = cgimin(&super, group) + blkstofrags(&super, groupInode / INOPB(&super));
  return static_cast<uint64_t>(fsbtodb(&super, inodeBlock)) * 0x200ULL +
         static_cast<uint64_t>(groupInode % INOPB(&super)) * sizeof(ufs2_dinode);
}

bool readUfsSuperblock(io::data::DataProvider* provider, fs& super, bool& needsSwap)
{
  if (!readProviderExact(provider, SBLOCK_UFS2, &super, sizeof(super))) {
    return false;
  }

  needsSwap = false;
  if (super.fs_magic == FS_UFS2_MAGIC) {
    return true;
  }

  if (swap32Value(static_cast<uint32_t>(super.fs_magic)) == FS_UFS2_MAGIC) {
    needsSwap = true;
    normalizeUfsSuperblock(super, true);
    return true;
  }

  return false;
}

void appendExtentRun(std::vector<UfsDataExtent>& extents, uint64_t offset, uint64_t length)
{
  if (length == 0) {
    return;
  }

  if (!extents.empty()) {
    auto& previous = extents.back();
    if (previous.offset + previous.length == offset) {
      previous.length += length;
      return;
    }
  }

  extents.push_back({ offset, length });
}

void appendDataBlockExtent(std::vector<UfsDataExtent>& extents, const fs& super, uint64_t block, uint64_t& remaining)
{
  if (block == 0 || remaining == 0) {
    return;
  }

  const auto offset = block * static_cast<uint64_t>(super.fs_fsize);
  const auto length = std::min<uint64_t>(static_cast<uint64_t>(super.fs_bsize), remaining);
  appendExtentRun(extents, offset, length);
  remaining -= length;
}

void collectIndirectExtents(
  io::data::DataProvider* provider,
  const fs& super,
  bool needsSwap,
  uint64_t tableBlock,
  int level,
  uint64_t& remaining,
  std::vector<UfsDataExtent>& extents,
  std::vector<uint64_t>& blockTables)
{
  if (tableBlock == 0 || level <= 0 || remaining == 0 || super.fs_bsize <= 0 || super.fs_nindir <= 0) {
    return;
  }

  const auto tableOffset = tableBlock * static_cast<uint64_t>(super.fs_fsize);
  blockTables.push_back(tableOffset);
  std::vector<uint64_t> table(static_cast<size_t>(super.fs_bsize) / sizeof(uint64_t));
  if (!readProviderExact(provider, tableOffset, table.data(), static_cast<uint32_t>(super.fs_bsize))) {
    return;
  }

  const auto count = std::min<int32_t>(super.fs_nindir, static_cast<int32_t>(table.size()));
  for (int32_t index = 0; index < count && remaining > 0; ++index) {
    auto block = needsSwap ? swap64Value(table[index]) : table[index];
    if (block == 0) {
      break;
    }

    if (level == 1) {
      appendDataBlockExtent(extents, super, block, remaining);
    }
    else {
      collectIndirectExtents(provider, super, needsSwap, block, level - 1, remaining, extents, blockTables);
    }
  }
}

std::vector<UfsDataExtent> collectInodeExtents(
  io::data::DataProvider* provider,
  const fs& super,
  bool needsSwap,
  const ufs2_dinode& inode,
  std::vector<uint64_t>& blockTables)
{
  std::vector<UfsDataExtent> extents;
  uint64_t remaining = inode.di_size;
  for (auto block : inode.di_db) {
    appendDataBlockExtent(extents, super, static_cast<uint64_t>(block), remaining);
    if (block == 0 || remaining == 0) {
      break;
    }
  }

  collectIndirectExtents(provider, super, needsSwap, static_cast<uint64_t>(inode.di_ib[0]), 1, remaining, extents, blockTables);
  collectIndirectExtents(provider, super, needsSwap, static_cast<uint64_t>(inode.di_ib[1]), 2, remaining, extents, blockTables);
  collectIndirectExtents(provider, super, needsSwap, static_cast<uint64_t>(inode.di_ib[2]), 3, remaining, extents, blockTables);
  return extents;
}

OffsetMetrics calculateExtentMetrics(const std::vector<UfsDataExtent>& extents, bool isDirectory)
{
  std::vector<uint64_t> offsets;
  offsets.reserve(extents.size());
  for (const auto& extent : extents) {
    offsets.push_back(extent.offset);
  }
  return calculateOffsetMetrics(offsets, isDirectory);
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
  auto output = listPartitionsFromDisk(disk.get());
  reportProgress(100, "Complete", "Partition list loaded");
  return output;
}

std::string displayPartitionFromDisk(disk::Disk* disk, const std::string& partitionName)
{
  auto partition = findPartition(disk, partitionName);

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

std::string displayPartition(const std::string& imagePath, const std::string& keyPath, const std::string& partitionName)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return displayPartitionFromDisk(disk.get(), partitionName);
}

std::string listFilesJsonFromDisk(disk::Disk* disk, const std::string& partitionName)
{
  reportProgress(15, "Detecting format", "PlayStation disk format detected");
  auto partition = findPartition(disk, partitionName);

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

std::string listFilesJson(const std::string& imagePath, const std::string& keyPath, const std::string& partitionName)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return listFilesJsonFromDisk(disk.get(), partitionName);
}

std::string decryptPartitionFromDisk(
  disk::Disk* disk,
  const std::string& partitionName,
  const std::string& outputPath)
{
  if (outputPath.empty()) {
    throw std::runtime_error("Output path is required.");
  }

  auto partition = findPartition(disk, partitionName);
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

std::string decryptPartition(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& outputPath)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return decryptPartitionFromDisk(disk.get(), partitionName, outputPath);
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

std::string exportFileFromDisk(
  disk::Disk* disk,
  const std::string& partitionName,
  const std::string& filePath,
  const std::string& outputPath)
{
  if (filePath.empty() || outputPath.empty()) {
    throw std::runtime_error("File path and output path are required.");
  }

  auto partition = findPartition(disk, partitionName);

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

std::string exportFile(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& filePath,
  const std::string& outputPath)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return exportFileFromDisk(disk.get(), partitionName, filePath, outputPath);
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

std::string recoverFilesJsonFromDisk(
  disk::Disk* disk,
  const std::string& partitionName,
  const std::string& outputRoot)
{
  if (outputRoot.empty()) {
    throw std::runtime_error("Output directory is required.");
  }

  ensureDirectory(outputRoot);
  auto partition = findPartition(disk, partitionName);
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

std::string recoverFilesJson(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  const std::string& outputRoot)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return recoverFilesJsonFromDisk(disk.get(), partitionName, outputRoot);
}

std::vector<UfsOrphanInode> scanOrphanInodes(disk::Partition* partition)
{
  reportProgress(-1, "Mounting file system", partition->getName());
  partition->mount();
  auto provider = partition->getDataProvider();
  fs super{};
  bool needsSwap = false;
  if (!readUfsSuperblock(provider, super, needsSwap)) {
    throw std::runtime_error("Selected PlayStation partition is not a readable UFS2 file system.");
  }

  if (super.fs_ncg == 0 || super.fs_ipg == 0 || super.fs_fsize <= 0 || super.fs_bsize <= 0 ||
      super.fs_ncg > 100000 || super.fs_ipg > 10000000) {
    throw std::runtime_error("UFS2 superblock values are outside expected bounds.");
  }

  const auto totalInodes64 = static_cast<uint64_t>(super.fs_ncg) * static_cast<uint64_t>(super.fs_ipg);
  const auto totalInodes = static_cast<uint32_t>(std::min<uint64_t>(totalInodes64, UINT32_MAX));
  std::vector<UfsOrphanInode> rows;
  const auto progressEvery = std::max<uint32_t>(1, totalInodes / 500);
  std::unordered_set<uint64_t> seenSignatures;

  for (uint32_t inodeNumber = 2; inodeNumber < totalInodes; ++inodeNumber) {
    if (inodeNumber % progressEvery == 0) {
      const auto percent = static_cast<int>((static_cast<uint64_t>(inodeNumber) * 90) / std::max<uint32_t>(1, totalInodes));
      reportProgress(percent, "Scanning deleted UFS inodes", std::to_string(inodeNumber));
    }

    const auto inodeOffset = inodeOffsetFor(super, inodeNumber);
    ufs2_dinode rawInode{};
    if (!readProviderExact(provider, inodeOffset, &rawInode, sizeof(rawInode))) {
      continue;
    }

    const auto inode = normalizeUfsInode(rawInode, needsSwap);
    const auto modeType = inode.di_mode & IFMT;
    if (modeType != IFREG || inode.di_nlink != 0 || inode.di_size == 0 || inode.di_size > partition->getLength()) {
      continue;
    }

    bool hasBlock = false;
    for (auto block : inode.di_db) {
      if (block > 0) {
        hasBlock = true;
        break;
      }
    }
    if (!hasBlock && inode.di_ib[0] <= 0 && inode.di_ib[1] <= 0 && inode.di_ib[2] <= 0) {
      continue;
    }

    std::vector<uint64_t> blockTables;
    auto extents = collectInodeExtents(provider, super, needsSwap, inode, blockTables);
    if (extents.empty()) {
      continue;
    }

    const auto signature = (extents.front().offset << 16) ^ inode.di_size ^ inodeNumber;
    if (!seenSignatures.insert(signature).second) {
      continue;
    }

    const auto metrics = calculateExtentMetrics(extents, false);
    UfsOrphanInode row;
    row.inodeNumber = inodeNumber;
    row.mode = inode.di_mode;
    row.linkCount = inode.di_nlink;
    row.size = inode.di_size;
    row.blocks = inode.di_blocks;
    row.created = inode.di_birthtime;
    row.modified = inode.di_mtime;
    row.accessed = inode.di_atime;
    row.inodeOffset = inodeOffset;
    row.dataOffsetCount = extents.size();
    row.dataRunCount = metrics.dataRunCount;
    row.largestRunBytes = metrics.largestRunBytes;
    row.estimatedAllocationUnit = metrics.estimatedAllocationUnit;
    row.dataRanges = metrics.dataRanges;
    row.extents = std::move(extents);
    rows.push_back(std::move(row));
  }

  reportProgress(100, "Complete", "Deleted UFS inode scan complete");
  return rows;
}

std::string listDeletedInodesJsonFromDisk(disk::Disk* disk, const std::string& partitionName)
{
  auto partition = findPartition(disk, partitionName);
  auto rows = scanOrphanInodes(partition);

  std::ostringstream output;
  output << "{\"partition\":";
  appendJsonEscaped(output, partitionName);
  output << ",\"files\":[";
  for (size_t index = 0; index < rows.size(); ++index) {
    const auto& row = rows[index];
    if (index > 0) {
      output << ',';
    }

    std::vector<uint64_t> offsets;
    offsets.reserve(std::min<size_t>(row.extents.size(), 4096));
    for (size_t extentIndex = 0; extentIndex < row.extents.size() && extentIndex < 4096; ++extentIndex) {
      offsets.push_back(row.extents[extentIndex].offset);
    }

    output << '{';
    output << "\"inode\":" << row.inodeNumber;
    output << ",\"path\":";
    appendJsonEscaped(output, "/.deleted/inode_" + std::to_string(row.inodeNumber));
    output << ",\"name\":";
    appendJsonEscaped(output, "inode_" + std::to_string(row.inodeNumber) + ".bin");
    output << ",\"type\":\"Deleted UFS inode\"";
    output << ",\"size\":" << row.size;
    output << ",\"created\":";
    appendJsonEscaped(output, formatDateTime(nullptr));
    output << ",\"modifiedUnix\":" << row.modified;
    output << ",\"accessedUnix\":" << row.accessed;
    output << ",\"inodeOffset\":";
    appendJsonEscaped(output, formatHex(row.inodeOffset));
    output << ",\"dataOffsetCount\":" << row.dataOffsetCount;
    output << ",\"dataRunCount\":" << row.dataRunCount;
    output << ",\"largestRunBytes\":" << row.largestRunBytes;
    output << ",\"estimatedAllocationUnit\":" << row.estimatedAllocationUnit;
    output << ",\"fragmentationStatus\":";
    appendJsonEscaped(output, row.dataRunCount <= 1 ? "Contiguous" : "Fragmented");
    output << ",\"dataRanges\":";
    appendJsonEscaped(output, row.dataRanges);
    output << ",\"dataOffsets\":";
    appendJsonEscaped(output, formatOffsetArray(offsets));
    output << '}';
  }
  output << "]}";
  return output.str();
}

std::string listDeletedInodesJson(const std::string& imagePath, const std::string& keyPath, const std::string& partitionName)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return listDeletedInodesJsonFromDisk(disk.get(), partitionName);
}

UfsOrphanInode findOrphanInode(disk::Partition* partition, uint32_t inodeNumber)
{
  auto provider = partition->getDataProvider();
  fs super{};
  bool needsSwap = false;
  if (!readUfsSuperblock(provider, super, needsSwap)) {
    throw std::runtime_error("Selected PlayStation partition is not a readable UFS2 file system.");
  }

  const auto inodeOffset = inodeOffsetFor(super, inodeNumber);
  ufs2_dinode rawInode{};
  if (!readProviderExact(provider, inodeOffset, &rawInode, sizeof(rawInode))) {
    throw std::runtime_error("Could not read UFS inode.");
  }

  const auto inode = normalizeUfsInode(rawInode, needsSwap);
  const auto modeType = inode.di_mode & IFMT;
  if (modeType != IFREG || inode.di_size == 0) {
    throw std::runtime_error("Requested UFS inode is not a recoverable regular file.");
  }

  std::vector<uint64_t> blockTables;
  auto extents = collectInodeExtents(provider, super, needsSwap, inode, blockTables);
  if (extents.empty()) {
    throw std::runtime_error("Requested UFS inode has no recoverable data blocks.");
  }

  UfsOrphanInode row;
  row.inodeNumber = inodeNumber;
  row.mode = inode.di_mode;
  row.linkCount = inode.di_nlink;
  row.size = inode.di_size;
  row.blocks = inode.di_blocks;
  row.inodeOffset = inodeOffset;
  row.extents = std::move(extents);
  return row;
}

std::string exportDeletedInodeFromDisk(
  disk::Disk* disk,
  const std::string& partitionName,
  uint32_t inodeNumber,
  const std::string& outputPath)
{
  if (outputPath.empty()) {
    throw std::runtime_error("Output path is required.");
  }

  auto partition = findPartition(disk, partitionName);
  partition->mount();
  auto provider = partition->getDataProvider();
  auto inode = findOrphanInode(partition, inodeNumber);
  ensureDirectory(pathDirectoryName(outputPath));

  std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
  if (!output.is_open()) {
    throw std::runtime_error("Failed to open output file: " + outputPath);
  }

  constexpr uint64_t maxBufferSize = 1024 * 1024;
  std::vector<char> buffer(static_cast<size_t>(std::min<uint64_t>(maxBufferSize, std::max<uint64_t>(1, inode.extents.front().length))));
  uint64_t remaining = inode.size;
  uint64_t written = 0;
  for (const auto& extent : inode.extents) {
    uint64_t consumed = 0;
    auto extentRemaining = std::min(extent.length, remaining);
    while (extentRemaining > 0) {
      const auto request = static_cast<uint32_t>(std::min<uint64_t>(buffer.size(), extentRemaining));
      provider->seek(static_cast<int64_t>(extent.offset + consumed));
      const auto readLength = provider->read(buffer.data(), request);
      if (readLength == 0) {
        throw std::runtime_error("Unexpected end of UFS deleted inode data.");
      }

      output.write(buffer.data(), static_cast<std::streamsize>(readLength));
      if (!output.good()) {
        throw std::runtime_error("Failed to write deleted inode output: " + outputPath);
      }

      consumed += readLength;
      extentRemaining -= readLength;
      remaining -= readLength;
      written += readLength;
    }

    if (remaining == 0) {
      break;
    }
  }

  if (remaining > 0) {
    throw std::runtime_error("Not enough UFS blocks remained to fully export the deleted inode.");
  }

  std::ostringstream message;
  message << "Exported deleted UFS inode " << inodeNumber << " to " << outputPath
          << " (" << written << " bytes).";
  return message.str();
}

std::string exportDeletedInode(
  const std::string& imagePath,
  const std::string& keyPath,
  const std::string& partitionName,
  uint32_t inodeNumber,
  const std::string& outputPath)
{
  reportProgress(0, "Opening image", imagePath);
  auto disk = openDisk(imagePath, keyPath);
  return exportDeletedInodeFromDisk(disk.get(), partitionName, inodeNumber, outputPath);
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

PSHDD_API int pshdd_list_deleted_inodes_json(
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

    return copyString(listDeletedInodesJson(image, key, partition), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_export_deleted_inode(
  const wchar_t* imagePath,
  const wchar_t* keyPath,
  const wchar_t* partitionName,
  uint32_t inodeNumber,
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
    if (image.empty() || key.empty() || partition.empty() || inodeNumber == 0 || destination.empty()) {
      return copyString("Image path, key path, partition name, inode number, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    return copyString(exportDeletedInode(image, key, partition, inodeNumber, destination), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_list_partitions_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    if (key.empty()) {
      return copyString("Key path is required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    reportProgress(75, "Reading partition table", "Listing PlayStation partitions");
    auto text = listPartitionsFromDisk(disk.get());
    reportProgress(100, "Complete", "Partition list loaded");
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

PSHDD_API int pshdd_display_partition_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    if (key.empty() || partition.empty()) {
      return copyString("Key path and partition name are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(displayPartitionFromDisk(disk.get(), partition), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_list_files_json_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    if (key.empty() || partition.empty()) {
      return copyString("Key path and partition name are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(listFilesJsonFromDisk(disk.get(), partition), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_decrypt_partition_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  const wchar_t* outputPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto destination = wideToUtf8(outputPath);
    if (key.empty() || partition.empty() || destination.empty()) {
      return copyString("Key path, partition name, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(decryptPartitionFromDisk(disk.get(), partition, destination), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_export_file_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  const wchar_t* filePath,
  const wchar_t* outputPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto source = wideToUtf8(filePath);
    const auto destination = wideToUtf8(outputPath);
    if (key.empty() || partition.empty() || source.empty() || destination.empty()) {
      return copyString("Key path, partition name, file path, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(exportFileFromDisk(disk.get(), partition, source, destination), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_recover_files_json_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  const wchar_t* outputDirectory,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto destination = wideToUtf8(outputDirectory);
    if (key.empty() || partition.empty() || destination.empty()) {
      return copyString("Key path, partition name, and output directory are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(recoverFilesJsonFromDisk(disk.get(), partition, destination), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_list_deleted_inodes_json_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    if (key.empty() || partition.empty()) {
      return copyString("Key path and partition name are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(listDeletedInodesJsonFromDisk(disk.get(), partition), output, outputLength, requiredBytes);
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

PSHDD_API int pshdd_export_deleted_inode_virtual(
  const wchar_t* imageLabel,
  const wchar_t* keyPath,
  void* context,
  ReadCallback readCallback,
  LengthCallback lengthCallback,
  const wchar_t* partitionName,
  uint32_t inodeNumber,
  const wchar_t* outputPath,
  char* output,
  int outputLength,
  int* requiredBytes)
{
  try {
    const auto label = wideToUtf8(imageLabel);
    const auto key = wideToUtf8(keyPath);
    const auto partition = wideToUtf8(partitionName);
    const auto destination = wideToUtf8(outputPath);
    if (key.empty() || partition.empty() || inodeNumber == 0 || destination.empty()) {
      return copyString("Key path, partition name, inode number, and output path are required.", output, outputLength, requiredBytes) == kBufferTooSmall
        ? kBufferTooSmall
        : kInvalidArgument;
    }

    reportProgress(0, "Opening virtual image", label);
    auto disk = openDiskFromCallbacks(label, key, context, readCallback, lengthCallback);
    return copyString(exportDeletedInodeFromDisk(disk.get(), partition, inodeNumber, destination), output, outputLength, requiredBytes);
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
  return 7;
}
