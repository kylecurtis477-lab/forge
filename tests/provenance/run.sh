#!/bin/sh
# tests/provenance: tools/provenance.forge (AI-provenance commit trailers, docs/PROVENANCE.md) on fixed messages,
# a mixed git-log dump, bad usage, and the repository's own history from the commit that added the tool.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tools/provenance.forge $T/p.s && build/fas -o $T/p.o $T/p.s && build/fld --base 0x100000000000 -o $T/prov $T/p.o || { echo "FAIL provenance build"; exit 1; }
# chk NAME WANT_RC WANT_TEXT MESSAGE
chk() {
  printf "$4" > $T/msg; $T/prov msg $T/msg > $T/out 2>&1; r=$?
  if [ $r = $2 ] && grep -q "$3" $T/out; then echo "PASS provenance-$1"; else echo "FAIL provenance-$1 (rc $r: $(head -1 $T/out))"; fail=1; fi
}
chk ai-generated 0 "0 without" "feat(x): y\n\nBody text.\n\nProvenance: ai-generated\nAI-Tool: Grok Bot\n"
chk human 0 "0 without" "fix: z\n\nProvenance: human\n"
chk ai-assisted 0 "0 without" "docs: a\n\nwhy\n\nAI-Tool: Grok Bot\nProvenance: ai-assisted\nReviewed-by: Kyle Curtis\n"
chk missing 1 "no Provenance trailer" "feat: no trailers\n\nJust a body.\n"
chk subject-only 1 "no Provenance trailer" "Provenance: human\n"
chk not-last-paragraph 1 "no Provenance trailer" "feat: a\n\nProvenance: human\n\nA closing paragraph.\n"
chk mixed-paragraph 1 "no Provenance trailer" "feat: a\n\nProvenance: human\nthis line is prose\n"
chk bad-kind 1 "is not one of" "feat: a\n\nProvenance: robot\n"
chk two-kinds 1 "more than one" "feat: a\n\nProvenance: human\nProvenance: ai-generated\nAI-Tool: x\n"
chk no-tool 1 "without an AI-Tool" "feat: a\n\nProvenance: ai-generated\n"
printf '@@commit 1111111111111111\nfeat: ok\n\nProvenance: human\n\n@@commit 2222222222222222\nfeat: bad\n\nno trailers\n\n@@commit 3333333333333333\nfix: ok\n\nProvenance: ai-generated\nAI-Tool: Grok Bot\n' > $T/log
$T/prov log $T/log > $T/out; r=$?
[ $r = 1 ] && grep -q "^provenance: 222222222222: no Provenance trailer" $T/out && grep -q "3 commit message(s) checked, 1 without" $T/out && echo "PASS provenance-log" || { echo "FAIL provenance-log"; fail=1; }
$T/prov > /dev/null 2>&1; [ $? = 2 ] && echo "PASS provenance-usage" || { echo "FAIL provenance-usage"; fail=1; }
# the repository itself: every commit from the one that added the tool on
if git rev-parse --git-dir >/dev/null 2>&1 && [ -n "$(git log --format=%H --diff-filter=A -- tools/provenance.forge)" ]; then
  first=$(git log --format=%H --diff-filter=A -- tools/provenance.forge | tail -1)
  git log --format='@@commit %H%n%B' "$first^..HEAD" > $T/repo
  $T/prov log $T/repo > $T/out 2>&1 && echo "PASS provenance-repo ($(tail -1 $T/out | cut -d' ' -f2) commits)" || { echo "FAIL provenance-repo"; cat $T/out; fail=1; }
else
  echo "SKIP provenance-repo (no git history with tools/provenance.forge)"
fi
exit $fail
