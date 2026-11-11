// TaoKeeperScan — C ABI 契约头
// 给 .NET P/Invoke 与 C 测试共用的平坦结构布局与导出函数声明。
// 布局契约：字段顺序两端必须一致；名字用定长缓冲
// （NTFS 文件名最长 255 个 UTF-16 字符，UTF-8 至多 765 字节 + NUL）。
#ifndef TK_INTEROP_ABI_H
#define TK_INTEROP_ABI_H

#include <stddef.h>
#include <stdint.h>

#define TK_NAME_BYTES 768

typedef struct TkMftFileName {
    uint64_t parent_ref;
    uint64_t created_100ns;
    uint64_t modified_100ns;
    uint64_t access_100ns;
    uint64_t allocated_size;
    uint64_t real_size;
    uint32_t is_directory;
    uint32_t reserved;
    char name_utf8[TK_NAME_BYTES]; /* NUL 结尾，UTF-8 */
} TkMftFileName;

typedef struct TkMftEntry {
    uint64_t record_no;
    TkMftFileName info;
} TkMftEntry;

typedef struct TkMftAggItem {
    uint64_t record_no;
    uint64_t recursive_size;
    uint64_t file_count;
} TkMftAggItem;

#ifdef __cplusplus
extern "C" {
#endif

/* 剥序：NTFS 引用值取记录号（低 48 位）。 */
uint64_t tk_mft_record_number(uint64_t ref_value);

/* 解析单条 MFT 记录；返回 1=成功 0=拒绝（坏魔数/损坏/无 $FILE_NAME）。 */
int tk_parse_mft_file_name(const uint8_t* data, size_t len, TkMftFileName* out);

/* 顺序遍历 $MFT 字节块：out/out_cap 调用方缓冲，needed_out 回填全部有效条数，
 * skipped_out 回填坏块数；返回实际写入条数。 */
size_t tk_walk_mft_records(const uint8_t* data, size_t len, size_t record_size,
                           TkMftEntry* out, size_t out_cap,
                           size_t* needed_out, size_t* skipped_out);

/* 空间分析聚合：entries 为 tk_walk_mft_records 产出的同布局平坦数组；
 * needed_out 回填全部目录聚合条数（>out_cap 时需扩缓冲重试），孤儿体积/文件数可空回填。 */
int tk_aggregate_dir_sizes(const TkMftEntry* entries, size_t count, uint64_t root_record,
                           TkMftAggItem* out, size_t out_cap,
                           size_t* needed_out, uint64_t* orphan_size_out, uint64_t* orphan_files_out);

/* $MFT 连续读取流式解码：create 返回不透明句柄（record_size=0 返回 null）。 */
void* tk_mft_stream_create(size_t record_size);
int tk_mft_stream_set_start_offset(void* handle, uint64_t bytes);
size_t tk_mft_stream_feed(void* handle, const uint8_t* data, size_t len, size_t* skipped_out);
size_t tk_mft_stream_take(void* handle, TkMftEntry* out, size_t out_cap);
uint64_t tk_mft_stream_pending(void* handle);
uint64_t tk_mft_stream_next_record_no(void* handle);
void tk_mft_stream_destroy(void* handle);

/* 分批增量聚合（大卷内存峰值优化）：目录节点常驻、文件体积即弃。
 * 会话非线程安全（单线程流式喂入）；快照非破坏可重复；
 * 产出与 tk_aggregate_dir_sizes 同构（只含连通到卷根且有文件贡献的目录，按记录号升序），
 * 孤儿=父链缺失/成环部分。handle 为 null 或 entries 为 null 时 feed 返回 0；
 * snapshot 返回写入条数，needed_out 回填全部条数（>out_cap 需扩缓冲重试）。 */
void* tk_mft_agg_incr_create(uint64_t root_record);
int tk_mft_agg_incr_feed(void* handle, const TkMftEntry* entries, size_t count);
size_t tk_mft_agg_incr_snapshot(void* handle, TkMftAggItem* out, size_t out_cap,
                                size_t* needed_out, uint64_t* orphan_size_out, uint64_t* orphan_files_out);
void tk_mft_agg_incr_destroy(void* handle);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TK_INTEROP_ABI_H */
