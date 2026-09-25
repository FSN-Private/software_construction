# Native rendering failure gate

Run `run-native.sh` on the trusted Linux runner with `DARKTABLE_IMAGE` set to an immutable digest. It requires a non-zero failure for missing source, malformed XMP, and unsupported source; it also verifies an enforced host timeout produces no derivative. The disposable workspace is removed on every exit path.
