#!/bin/sh
set -eu

include_file=/tmp/paparr-trusted-proxies.conf
temporary_file="${include_file}.tmp"

umask 077
: > "$temporary_file"
remaining=${UI_TRUSTED_PROXY_NETWORKS:-}
while [ -n "$remaining" ]; do
    case "$remaining" in
        *';'*) cidr=${remaining%%;*}; remaining=${remaining#*;} ;;
        *) cidr=$remaining; remaining= ;;
    esac

    case "$cidr" in
        */*) address=${cidr%%/*}; prefix=${cidr#*/} ;;
        *) echo 'UI_TRUSTED_PROXY_NETWORKS entries must be CIDRs.' >&2; exit 1 ;;
    esac
    case "$prefix" in
        ''|*[!0-9]*|*/*) echo 'UI_TRUSTED_PROXY_NETWORKS entries must have a numeric CIDR prefix.' >&2; exit 1 ;;
    esac
    case "$address" in
        ''|*[!0123456789abcdefABCDEF:.]*) echo 'UI_TRUSTED_PROXY_NETWORKS contains an invalid CIDR address.' >&2; exit 1 ;;
    esac

    printf '%s 1;\n' "$cidr" >> "$temporary_file"
done

mv "$temporary_file" "$include_file"
nginx -t
exec /docker-entrypoint.sh "$@"
