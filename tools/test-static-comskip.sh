#!/bin/sh
# Smoke-tests out/comskip on generated broadcast-style samples (run in a plain Debian container
# so it proves the binary needs nothing from its environment).
set -eux
mkdir -p /tmp/t && cd /tmp/t
cp /work/test-samples/*.ts . 
printf 'detect_method=111\noutput_edl=1\noutput_ffmeta=0\nverbose=1\n' > test.ini
for f in *.ts; do
  set +e
  /work/out/comskip --ini=test.ini --output=/tmp/t "$f" > "$f.out" 2>&1
  code=$?
  set -e
  echo "== $f exit=$code"; tail -n 15 "$f.out"
  [ "$code" -le 1 ] || { echo "comskip failed on $f"; exit 1; }
  if grep -qiE "could not|unsupported|no decoder|failed to open" "$f.out"; then echo "decode problem on $f"; exit 1; fi
done
