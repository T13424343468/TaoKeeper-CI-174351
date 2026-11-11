// TaoKeeperScan — NTFS MFT 记录解析器（MFT 直读路径的结构层）
// 用途：Windows 真机直读 $MFT 后，把原始记录字节解为文件名/父目录/大小。
// 沙箱可用合成记录实测解析正确性；真机侧只负责取回记录字节（FSCTL/扇区读）。
#pragma once

#include <cstdint>
#include <deque>
#include <map>
#include <string>
#include <unordered_map>
#include <vector>

namespace tk {

/// <summary>一条 MFT 记录中解析出的文件名属性（$FILE_NAME）。</summary>
struct MftFileName {
    /// <summary>父目录 MFT 引用（低 48 位为记录号）。</summary>
    uint64_t parent_ref = 0;
    /// <summary>文件名（UTF-8，转换自 UTF-16LE）。</summary>
    std::string name_utf8;
    /// <summary>实际大小（字节）。</summary>
    uint64_t real_size = 0;
    /// <summary>分配大小（字节，簇对齐）。</summary>
    uint64_t allocated_size = 0;
    /// <summary>是否目录。</summary>
    bool is_directory = false;
    /// <summary>标准时间：创建/修改/MFT修改/访问，Windows FILETIME（100ns 自 1601-01-01）。</summary>
    uint64_t created_100ns = 0;
    uint64_t modified_100ns = 0;
    uint64_t access_100ns = 0;
};

/// <summary>
/// 解析一条已取回的 NTFS MFT 记录（通常 1024 字节）。
/// 步骤：校验 "FILE" 魔数 → 应用更新序列阵列（USA fixup，扇区尾两字节校正）→
/// 遍历属性找 $FILE_NAME（0x30）→ 解出父引用、UTF-8 文件名、大小、目录位与时间。
/// 校验不过（魔数错、fixup 不符、结构越界）返回 false，绝不带错数据返回。
/// </summary>
bool ParseMftFileName(const uint8_t* data, size_t len, MftFileName& out);

/// <summary>UTF-16LE → UTF-8（解析器内部使用；独立导出便于测试）。</summary>
std::string Utf16LeToUtf8(const uint8_t* data, size_t bytes);

/// <summary>NTFS 引用值（MFT 引用/父引用）取记录号：低 48 位，高 16 位是序列号须剥掉。</summary>
constexpr uint64_t MftRecordNumber(uint64_t ref_value) {
    return ref_value & 0x0000FFFFFFFFFFFFull;
}

/// <summary>顺序扫描产出：记录块序号（即 MFT 记录号）+ 该记录解析出的文件名属性。</summary>
struct MftEntry {
    uint64_t record_no = 0;
    MftFileName info;
};

/// <summary>
/// 顺序遍历 $MFT 字节块（真机直读或沙箱合成，按固定记录大小切分）。
/// 每块按 ParseMftFileName 同口径校验解析；成功进 out，坏记录
/// （坏魔数/损坏/无 $FILE_NAME 元数据记录）跳过并计入 skipped，绝不中断整块。
/// 尾部长度不足一个记录的残块忽略。返回写入 out 的条数。
/// </summary>
size_t WalkMftRecords(const uint8_t* data, size_t len, size_t record_size,
                      std::vector<MftEntry>& out, size_t& skipped);

/// <summary>
/// $MFT 连续读取解码器（真机分块直读的流式口径）：
/// 真机无法一次读全 $MFT，只能按缓冲区分块取回字节流；本解码器按序喂入任意大小的块，
/// 跨块拼接残记录，逐条按 WalkMftRecords 同口径校验解析，记录号按绝对块序连续编号
/// （坏记录也占号，与整块遍历口径一致）。首块之前可跳过流起始的前缀字节
/// （如按簇对齐读多出的部分）。待取条目存内部队列，由 Take 逐批取走。
/// </summary>
class MftStreamDecoder {
public:
    /// <summary>record_size 为 MFT 记录大小（通常 1024）；0 视为配置错误，Feed 一律拒绝。</summary>
    explicit MftStreamDecoder(size_t record_size);

    /// <summary>设置流起始要跳过的前缀字节数（须在首次 Feed 前设置；首次 Feed 后再设不生效）。</summary>
    void SetStartOffset(uint64_t bytes);

    /// <summary>
    /// 喂入一块按序读到的字节（块大小任意）。
    /// 返回喂完后待取条目数；本块新解析出的坏记录数写入 skipped（可空）。
    /// </summary>
    size_t Feed(const uint8_t* data, size_t len, size_t& skipped);

    /// <summary>取走至多 max_count 条待取条目（默认全部取走，队列清空）。返回取走条数。</summary>
    size_t Take(std::vector<MftEntry>& out, size_t max_count = static_cast<size_t>(-1));

    /// <summary>待取条目数（未及时 Take 时积存在内部队列）。</summary>
    size_t PendingEntries() const { return queue_.size(); }

    /// <summary>残块字节数（不足一条记录，等下一块拼接）。</summary>
    size_t PendingBytes() const { return carry_.size(); }

    /// <summary>下一条记录将获得的记录号（坏记录也占号）。</summary>
    uint64_t NextRecordNo() const { return next_record_no_; }

private:
    size_t record_size_;
    std::vector<uint8_t> carry_;
    std::deque<MftEntry> queue_;
    uint64_t start_skip_ = 0;
    bool started_ = false;
    uint64_t next_record_no_ = 0;
};

/// <summary>
/// 从记录号映射构建完整路径（父链上溯，'\\' 分隔；记录 5 为卷根，根名不拼入）。
/// 任一环缺失（父记录不在映射内）或父子互指成环返回 false，防死循环；
/// 调用方可退回单名展示。相对序号：记录号与父引用都按低 48 位对齐。
/// </summary>
bool BuildFullPath(const std::map<uint64_t, MftEntry>& entries,
                   uint64_t record_no, std::string& out_path);

/// <summary>一个目录记录的体积聚合产出。</summary>
struct MftDirAggregates {
    /// <summary>递归体积：本目录（含子目录链）下所有文件的实际大小之和。</summary>
    uint64_t recursive_size = 0;
    /// <summary>递归文件数。</summary>
    uint64_t file_count = 0;
};

/// <summary>整卷聚合结果：每个出现过的记录的聚合 + 挂不进映射的孤儿体积（父链缺失/成环部分）。</summary>
struct MftAggregateResult {
    std::map<uint64_t, MftDirAggregates> per_record;
    uint64_t orphan_size = 0;
    uint64_t orphan_files = 0;
};

/// <summary>
/// 空间分析聚合（M1 空间分析树的后端逻辑）：把每个文件的实际大小沿父链上卷到每一级目录。
/// 文件指非目录记录；目录自身不贡献体积。父链走到 root_record（卷根，默认 5）即止，
/// root 也在映射内时同样计入；中途缺失或成环的部分记入 orphan，绝不中断整卷聚合。
/// </summary>
MftAggregateResult AggregateDirSizes(const std::map<uint64_t, MftEntry>& entries,
                                     uint64_t root_record = 5);

/// <summary>
/// 分批增量聚合器（大卷内存峰值优化）：随流式解码逐批喂入已解析条目，
/// 只常驻目录节点（真实卷约占条目 1%），文件体积沿父链计入后即弃——
/// 不保留文件记录，内存峰值从「全部条目」降到「目录节点 + 待父桶」。
/// 快照（TakeSnapshot）非破坏、可重复，产出与一次性 AggregateDirSizes 同构：
/// 只输出连通到卷根且有文件贡献的目录（按记录号升序），父链缺失/成环部分计孤儿。
/// 口径差异（畸形数据，正常卷无差异）：父记录为文件记录时按孤儿处理，
/// 不沿文件链透传；同一条目重复喂入按重复计数，批次须来自流式解码等唯一来源。
/// </summary>
class MftIncrementalAggregator {
public:
    explicit MftIncrementalAggregator(uint64_t root_record = 5);

    /// <summary>喂入一条已解析条目；体积即时挂到父目录（父未出现时挂待父桶，父到达后归并）。</summary>
    void Feed(const MftEntry& entry);

    /// <summary>喂入一批（顺序任意；父记录可晚于子记录出现）。</summary>
    void Feed(const std::vector<MftEntry>& entries);

    /// <summary>当前快照：连通目录的递归聚合（记录号升序）+ 孤儿合计。非破坏，可重复调用。</summary>
    MftAggregateResult TakeSnapshot() const;

private:
    struct DirNode {
        uint64_t parent = 0;       // 剥序后的父记录号
        uint64_t direct_size = 0;  // 直接子文件的体积合计（子目录经快照期归并）
        uint64_t direct_files = 0;
    };

    std::unordered_map<uint64_t, DirNode> dirs_;
    /// <summary>待父桶：等待记录号作为目录出现的文件合计（父一直不出现即孤儿）。</summary>
    std::unordered_map<uint64_t, std::pair<uint64_t, uint64_t>> pending_;
    uint64_t root_record_;
};

} // namespace tk
