// TaoKeeperScan — NTFS MFT 记录解析器实现（可移植 C++20，无平台依赖）
#include "tk/mft.hpp"

#include <algorithm>
#include <cstring>
#include <unordered_set>

namespace tk {

namespace {

inline uint16_t Rd16(const uint8_t* p) {
    return static_cast<uint16_t>(p[0] | (p[1] << 8));
}
inline uint32_t Rd32(const uint8_t* p) {
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8)
         | (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}
inline uint64_t Rd64(const uint8_t* p) {
    uint64_t lo = Rd32(p);
    uint64_t hi = Rd32(p + 4);
    return lo | (hi << 32);
}

/// <summary>应用更新序列阵列（USA fixup）：每个 512 字节扇区末两字节若与校验值相等，
/// 替换为阵列中对应校正值；任一扇区不匹配即判记录损坏。写回发生在调用方提供的本地副本上，
/// 原始输入缓冲绝不改写（同一缓冲可重复解析，见 ParseMftFileName）。</summary>
bool ApplyFixup(uint8_t* data, size_t len, size_t& error_at) {
    const size_t usn_off = Rd16(data + 0x30);
    const size_t usn_count = Rd16(data + 0x32);
    if (usn_off == 0 || usn_count < 1) return true; // 无阵列：按原样接受
    if (usn_off + usn_count * 2 > len) { error_at = usn_off; return false; }

    const size_t sector = 512;
    if (len % sector != 0 || usn_count - 1 != len / sector) { error_at = usn_off; return false; }

    const uint16_t check = Rd16(data + usn_off);
    for (size_t i = 1; i < usn_count; ++i) {
        const size_t sector_end = i * sector;             // 第 i 扇区末尾
        const size_t pos = sector_end - 2;
        const uint16_t stored = Rd16(data + pos);
        if (stored != check) { error_at = pos; return false; }
        const uint16_t fix = Rd16(data + usn_off + i * 2);
        data[pos] = static_cast<uint8_t>(fix & 0xFF);
        data[pos + 1] = static_cast<uint8_t>(fix >> 8);
    }
    return true;
}

} // namespace

std::string Utf16LeToUtf8(const uint8_t* data, size_t bytes) {
    std::string out;
    out.reserve(bytes / 2);
    for (size_t i = 0; i + 1 < bytes; i += 2) {
        uint32_t cp = static_cast<uint32_t>(Rd16(data + i));
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 3 < bytes) {
            const uint32_t lo = Rd16(data + i + 2);
            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                i += 2;
            }
        }
        if (cp < 0x80) out.push_back(static_cast<char>(cp));
        else if (cp < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }
    return out;
}

bool ParseMftFileName(const uint8_t* data, size_t len, MftFileName& out) {
    if (data == nullptr || len < 56 || std::memcmp(data, "FILE", 4) != 0) return false;

    // fixup 写回在本地副本上进行：输入缓冲保持只读，同一字节流可重复解析
    // （WalkMftRecords 与流式解码对同一缓冲先后解析互不影响；真机重试路径也不串号）。
    std::vector<uint8_t> buf(data, data + len);
    size_t error_at = 0;
    if (!ApplyFixup(buf.data(), len, error_at)) return false;
    data = buf.data();

    // 遍历属性链：首个属性偏移在 0x14。
    size_t off = Rd16(data + 0x14);
    while (off + 16 <= len) {
        const uint32_t type = Rd32(data + off);
        if (type == 0xFFFFFFFFu) break; // 结束标记
        const uint32_t attr_len = Rd32(data + off + 4);
        if (attr_len < 16 || off + attr_len > len) return false;

        const bool resident = data[off + 8] == 0;
        if (type == 0x30u && resident) { // $FILE_NAME
            if (off + 24 > len) return false;
            const uint32_t value_len = Rd32(data + off + 16);
            const uint16_t value_off = Rd16(data + off + 20);
            const size_t vpos = off + value_off;
            if (value_len < 66 || vpos + value_len > len) return false;

            const uint8_t* v = data + vpos;
            out.parent_ref      = Rd64(v);
            out.created_100ns   = Rd64(v + 8);
            out.modified_100ns  = Rd64(v + 16);
            out.access_100ns    = Rd64(v + 32);
            out.allocated_size  = Rd64(v + 40);
            out.real_size       = Rd64(v + 48);
            const uint32_t flags = Rd32(v + 56);
            out.is_directory    = (flags & 0x1u) != 0;
            const uint8_t name_chars = v[64];
            if (value_len < 66u + 2u * name_chars) return false;
            out.name_utf8 = Utf16LeToUtf8(v + 66, 2u * name_chars);
            return true;
        }
        off += attr_len;
    }
    return false; // 没有 $FILE_NAME 属性（如 $MFT 自身元数据记录）
}

size_t WalkMftRecords(const uint8_t* data, size_t len, size_t record_size,
                      std::vector<MftEntry>& out, size_t& skipped) {
    out.clear();
    skipped = 0;
    if (data == nullptr || record_size == 0) return 0;
    const size_t blocks = len / record_size; // 残块忽略
    for (size_t i = 0; i < blocks; ++i) {
        const uint8_t* rec = data + i * record_size;
        MftFileName parsed;
        if (ParseMftFileName(rec, record_size, parsed)) {
            out.push_back(MftEntry{i, parsed});
        } else {
            ++skipped; // 坏记录/元数据记录：跳过不中断
        }
    }
    return out.size();
}

// ---- $MFT 连续读取解码器 ----

MftStreamDecoder::MftStreamDecoder(size_t record_size) : record_size_(record_size) {}

void MftStreamDecoder::SetStartOffset(uint64_t bytes) {
    if (!started_) start_skip_ = bytes; // 首次 Feed 后再设不生效（口径见头文件）
}

size_t MftStreamDecoder::Feed(const uint8_t* data, size_t len, size_t& skipped) {
    skipped = 0;
    if (data == nullptr || record_size_ == 0) return queue_.size();
    started_ = true;

    // 先消费起始前缀跳过量，再把余下字节并入残块缓冲。
    size_t off = 0;
    if (start_skip_ > 0) {
        const uint64_t take = start_skip_ < len ? start_skip_ : len;
        off = static_cast<size_t>(take);
        start_skip_ -= take;
    }
    carry_.insert(carry_.end(), data + off, data + len);

    // 逐条切分：完整记录才解析，坏记录同样占号（与整块遍历的绝对序号口径一致）。
    while (carry_.size() >= record_size_) {
        MftFileName parsed;
        if (ParseMftFileName(carry_.data(), record_size_, parsed)) {
            queue_.push_back(MftEntry{next_record_no_, std::move(parsed)});
        } else {
            ++skipped;
        }
        ++next_record_no_;
        carry_.erase(carry_.begin(), carry_.begin() + static_cast<long>(record_size_));
    }
    return queue_.size();
}

size_t MftStreamDecoder::Take(std::vector<MftEntry>& out, size_t max_count) {
    out.clear();
    while (!queue_.empty() && out.size() < max_count) {
        out.push_back(std::move(queue_.front()));
        queue_.pop_front();
    }
    return out.size();
}

bool BuildFullPath(const std::map<uint64_t, MftEntry>& entries,
                   uint64_t record_no, std::string& out_path) {
    out_path.clear();
    const uint64_t start = MftRecordNumber(record_no);
    const auto self = entries.find(start);
    if (self == entries.end()) return false;

    std::vector<std::string> names; // 自底向上收集，最后反转
    uint64_t cur = start;
    std::map<uint64_t, bool> visited; // 父子互指成环即拒绝
    while (cur != 5) {                // 5 = 卷根，根名不拼入
        if (visited.count(cur)) return false;
        visited[cur] = true;
        const auto it = entries.find(cur);
        if (it == entries.end()) return false; // 父链缺失，交调用方退回单名
        names.push_back(it->second.info.name_utf8);
        const uint64_t parent = MftRecordNumber(it->second.info.parent_ref);
        if (parent == cur) return false; // 自指
        cur = parent;
    }
    out_path.reserve(names.size());
    for (size_t i = names.size(); i-- > 0;) {
        out_path += names[i];
        if (i > 0) out_path += '\\';
    }
    return true;
}

MftAggregateResult AggregateDirSizes(const std::map<uint64_t, MftEntry>& entries,
                                     uint64_t root_record) {
    MftAggregateResult result;
    std::vector<uint64_t> chain; // 单文件的上卷路径（自底向上）
    chain.reserve(64);
    for (const auto& [record_no, entry] : entries) {
        if (entry.info.is_directory) continue; // 目录自身不贡献体积
        chain.clear();
        uint64_t cur = record_no;
        std::map<uint64_t, bool> visited;
        bool reached_root = false;
        while (true) {
            if (visited.count(cur)) break; // 成环：剩余部分记孤儿
            visited[cur] = true;
            const auto it = entries.find(cur);
            if (it != entries.end()) {
                if (it->second.info.is_directory) chain.push_back(cur); // 只有目录参与上卷
            } else if (cur != root_record) {
                break; // 父链缺失：剩余部分记孤儿
            }
            if (cur == root_record) { reached_root = true; break; } // 卷根：上卷到此为止
            if (it == entries.end()) break; // 已记孤儿，止步
            const uint64_t parent = MftRecordNumber(it->second.info.parent_ref);
            if (parent == cur) break;
            cur = parent;
        }
        if (reached_root) {
            for (uint64_t dir_no : chain) {
                auto& agg = result.per_record[dir_no];
                agg.recursive_size += entry.info.real_size;
                agg.file_count += 1;
            }
        } else {
            result.orphan_size += entry.info.real_size;
            result.orphan_files += 1;
        }
    }
    return result;
}

// ---- 分批增量聚合器：目录节点常驻、文件即弃、快照期归并（口径见 mft.hpp 类注释） ----

MftIncrementalAggregator::MftIncrementalAggregator(uint64_t root_record)
    : root_record_(root_record) {}

void MftIncrementalAggregator::Feed(const MftEntry& entry) {
    const uint64_t record = entry.record_no;
    const uint64_t parent = MftRecordNumber(entry.info.parent_ref);
    const uint64_t size = entry.info.real_size;

    if (entry.info.is_directory) {
        auto& node = dirs_[record];          // 已存在时父引用按后到覆盖（与一次性 map 口径一致）
        node.parent = parent;
        // 早到的文件挂在本记录的待父桶里，目录一出现即归并为直接合计
        auto pit = pending_.find(record);
        if (pit != pending_.end()) {
            node.direct_size += pit->second.first;
            node.direct_files += pit->second.second;
            pending_.erase(pit);
        }
        return;
    }

    // 文件：体积挂到父记录——父已是目录进直接合计；否则挂待父桶，等父到达再归并
    auto dit = dirs_.find(parent);
    if (dit != dirs_.end()) {
        dit->second.direct_size += size;
        dit->second.direct_files += 1;
    } else {
        auto& bucket = pending_[parent];
        bucket.first += size;
        bucket.second += 1;
    }
}

void MftIncrementalAggregator::Feed(const std::vector<MftEntry>& entries) {
    for (const auto& entry : entries) Feed(entry);
}

MftAggregateResult MftIncrementalAggregator::TakeSnapshot() const {
    MftAggregateResult result;

    // 连通性：从每个目录沿父链上行，抵达卷根=连通；父缺失或成环=断开。
    // 按快照期状态缓存（0=未知 1=断开 2=连通），路径上的节点共享同一结论。
    enum : uint8_t { kUnknown = 0, kDisconnected = 1, kConnected = 2 };
    std::unordered_map<uint64_t, uint8_t> state;
    state.reserve(dirs_.size() * 2);

    for (const auto& [record, node] : dirs_) {
        (void)node;
        if (state.count(record)) continue;
        std::vector<uint64_t> path;                          // 单次上行的路径缓存
        std::unordered_set<uint64_t> seen;                   // 本条链的成环守卫
        uint8_t verdict = kUnknown;
        uint64_t cur = record;
        while (true) {
            if (cur == root_record_) { verdict = kConnected; break; }
            auto sit = state.find(cur);
            if (sit != state.end()) { verdict = sit->second; break; }
            auto dit = dirs_.find(cur);
            if (dit == dirs_.end()) { verdict = kDisconnected; break; } // 父缺失
            if (!seen.insert(cur).second) { verdict = kDisconnected; break; } // 成环
            path.push_back(cur);
            cur = dit->second.parent;
        }
        for (uint64_t no : path) state[no] = verdict;
        state[record] = verdict;
    }

    // 孤儿：未解析的待父桶 + 断开目录的直接合计（文件体积恒挂在直接父上，各计一次）
    for (const auto& [record, bucket] : pending_) {
        (void)record;
        result.orphan_size += bucket.first;
        result.orphan_files += bucket.second;
    }
    for (const auto& [record, node] : dirs_) {
        if (state[record] != kConnected) {
            result.orphan_size += node.direct_size;
            result.orphan_files += node.direct_files;
        }
    }

    // 递归归并：连通子树内自底向上（子目录合计并入每个祖先的递归值）。
    // 迭代后序：显式栈避免深链递归溢出；断开子树不计入产出（其体积已计孤儿）。
    struct Frame {
        uint64_t node;
        size_t child_index;
    };
    std::unordered_map<uint64_t, std::vector<uint64_t>> children;
    children.reserve(dirs_.size());
    for (const auto& [record, node] : dirs_) {
        if (state[record] == kConnected && record != root_record_)
            children[node.parent].push_back(record);
    }
    for (auto& [parent, kids] : children) {
        (void)parent;
        std::sort(kids.begin(), kids.end()); // 产出确定性：后序归并顺序固定
    }

    std::vector<uint64_t> connected;
    connected.reserve(dirs_.size());
    for (const auto& [record, verdict] : state)
        if (verdict == kConnected) connected.push_back(record);
    std::sort(connected.begin(), connected.end());

    std::unordered_map<uint64_t, std::pair<uint64_t, uint64_t>> rec; // 记录号→(递归体积, 递归文件数)
    rec.reserve(connected.size() * 2);
    std::vector<Frame> stack;
    for (uint64_t start : connected) {
        if (rec.count(start)) continue; // 已随祖先链归并过
        stack.push_back({start, 0});
        while (!stack.empty()) {
            auto& top = stack.back();
            if (top.child_index == 0) {
                const auto& node = dirs_.find(top.node)->second;
                rec[top.node] = {node.direct_size, node.direct_files}; // 首入栈：落直接合计
            }
            auto& kids = children[top.node];
            if (top.child_index < kids.size()) {
                uint64_t child = kids[top.child_index++];
                if (!rec.count(child)) stack.push_back({child, 0});
            } else {
                uint64_t size = rec[top.node].first;
                uint64_t files = rec[top.node].second;
                stack.pop_back();
                if (!stack.empty()) { // 向上并入父；起始节点无外层父时值即终值
                    uint64_t ancestor = stack.back().node;
                    rec[ancestor].first += size;
                    rec[ancestor].second += files;
                }
            }
        }
    }
    for (uint64_t no : connected) {
        auto it = rec.find(no);
        if (it == rec.end()) continue;
        if (it->second.first == 0 && it->second.second == 0) continue; // 空目录（无文件贡献）不进产出，与一次性聚合同口径
        auto& agg = result.per_record[no];
        agg.recursive_size = it->second.first;
        agg.file_count = it->second.second;
    }
    return result;
}

} // namespace tk
