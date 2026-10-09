# Characterization fixtures

`shortcuts-supported.hex` is a synthetic 269-byte binary VDF represented as hex
for reviewability. It was constructed independently of the C# serializer using
UTF-8 NUL-terminated strings and Python `struct.pack("<I", value)` for Int32
bytes. It contains two shortcuts, a quoted path with spaces, a high-bit AppID,
Unicode text, tags, an empty map, and an unrelated custom field.

It is **not captured from Steam** and does not establish Steam's AppID/quoting
behavior. Tests exercise the production codec and root serializer without loading
the application entrypoint or accessing Steam userdata. AppID expectations were
calculated independently using Python `zlib.crc32` and UTF-8.

The codec now rejects invalid root/trailers, unsupported types, invalid UTF-8,
truncated data, and excessive file/string/depth/element sizes. It accepts a root
close with or without one extra end marker; saves emit the established extra
marker. Unknown field names with supported types are retained. More VDF types
must not be silently discarded; add coverage before supporting them.

Repository tests use temporary copies and injected failures around actual file
operations. A Windows-only test exercises a real sharing violation at replacement
and skips on other OSes. These tests do not exercise live Steam. Future Steam-captured fixtures must be
sanitized, provenance documented, and stored here rather than using live userdata.
