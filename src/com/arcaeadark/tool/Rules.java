package com.arcaeadark.tool;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Replacement rule set: parses dark_rules.json and builds a replacement plan. */
public final class Rules {
    public static final class Pair {
        public String from = "";
        public String to = "";
        public String note;
    }

    public static final class PatternRule {
        public String match = "";
        public String replace = "";
        public String note;
    }

    public static final class Replacement {
        public final String from;
        public final String to;
        public final String kind;
        public final String note;
        public Replacement(String from, String to, String kind, String note) {
            this.from = from; this.to = to; this.kind = kind; this.note = note;
        }
    }

    public static final class SkipRecord {
        public final String from;
        public final String to;
        public final String reason;
        public SkipRecord(String from, String to, String reason) {
            this.from = from; this.to = to; this.reason = reason;
        }
    }

    public static final class Plan {
        public final List<Replacement> replacements = new ArrayList<Replacement>();
        public final List<SkipRecord> skips = new ArrayList<SkipRecord>();
        public final List<String> unhandledSideSources = new ArrayList<String>();
        public int totalEntries;
        public long estimatedTargetBytes;
    }

    public int version = 1;
    public String description;
    public String targetSide;
    public final List<Pair> pairs = new ArrayList<Pair>();
    public final List<PatternRule> patterns = new ArrayList<PatternRule>();

    private final List<Object[]> compiled = new ArrayList<Object[]>();

    private static final String[] SIDE_ASSET_DIRS = {
            "assets/img/", "assets/models/", "assets/particle/", "assets/layouts/",
    };

    private static final Pattern SIDE_SOURCE_TOKEN = Pattern.compile(
            "_light(?![a-z])|-light(?![a-z])|_colorless|_colourless|-colorless|-colourless"
                    + "|_lephon(?![a-z])|-lephon(?![a-z])",
            Pattern.CASE_INSENSITIVE);

    private static final Pattern DARK_TOKEN = Pattern.compile(
            "dark|conflict|_conf(?![a-z])|-conf(?![a-z])", Pattern.CASE_INSENSITIVE);

    @SuppressWarnings("unchecked")
    public static Rules parse(String jsonText) {
        Map<String, Object> root = Json.parseObject(jsonText);
        Rules rs = new Rules();
        Object v = root.get("version");
        if (v instanceof Number) rs.version = ((Number) v).intValue();
        rs.description = str(root.get("description"));
        rs.targetSide = str(root.get("targetSide"));
        Object pv = root.get("pairs");
        if (pv instanceof List) {
            for (Object o : (List<Object>) pv) {
                if (!(o instanceof Map)) continue;
                Map<String, Object> m = (Map<String, Object>) o;
                Pair p = new Pair();
                p.from = str(m.get("from"));
                p.to = str(m.get("to"));
                p.note = str(m.get("note"));
                rs.pairs.add(p);
            }
        }
        Object rv = root.get("patterns");
        if (rv instanceof List) {
            for (Object o : (List<Object>) rv) {
                if (!(o instanceof Map)) continue;
                Map<String, Object> m = (Map<String, Object>) o;
                PatternRule p = new PatternRule();
                p.match = str(m.get("match"));
                p.replace = str(m.get("replace"));
                p.note = str(m.get("note"));
                rs.patterns.add(p);
            }
        }
        rs.compile();
        return rs;
    }

    private static String str(Object o) { return o == null ? null : o.toString(); }

    public void compile() {
        compiled.clear();
        for (PatternRule p : patterns) {
            if (isBlank(p.match) || isBlank(p.replace)) continue;
            compiled.add(new Object[] { Pattern.compile(p.match), p.replace, p.note });
        }
    }

    private static boolean isBlank(String s) { return s == null || s.trim().isEmpty(); }

    /** Builds the plan from the source APK entry list (all content comes from the source APK). */
    public Plan buildPlan(ZipReader apk) {
        Plan plan = new Plan();
        plan.totalEntries = apk.entryCount();
        Set<String> handled = new HashSet<String>();

        for (Pair p : pairs) {
            if (isBlank(p.from) || isBlank(p.to)) continue;

            if (!apk.has(p.from)) {
                plan.skips.add(new SkipRecord(p.from, p.to, "source file not in this APK (possibly different version), skipped"));
                continue;
            }
            if (!apk.has(p.to)) {
                plan.skips.add(new SkipRecord(p.from, p.to, "target (conflict-side) file missing, skipped"));
                continue;
            }
            if (!handled.add(p.from)) continue;
            plan.replacements.add(new Replacement(p.from, p.to, "pair", p.note));
        }

        Set<String> patternTargetMissing = new HashSet<String>();
        for (Object[] c : compiled) {
            Pattern rx = (Pattern) c[0];
            String replace = (String) c[1];
            String note = (String) c[2];
            for (String entry : apk.names()) {
                if (handled.contains(entry)) continue;
                Matcher m = rx.matcher(entry);
                if (!m.find()) continue;

                String target = m.replaceAll(replace);
                if (target.equals(entry)) continue;

                if (!apk.has(target)) {
                    String key = entry + " => " + target;
                    if (patternTargetMissing.add(key)) {
                        plan.skips.add(new SkipRecord(entry, target, "target (conflict-side) file missing, skipped"));
                    }
                    continue;
                }
                if (!handled.add(entry)) continue;
                plan.replacements.add(new Replacement(entry, target, "pattern", note));
            }
        }

        for (Replacement r : plan.replacements) {
            ZipReader.Entry e = apk.get(r.to);
            if (e != null) plan.estimatedTargetBytes += e.size;
        }

        for (String entry : apk.names()) {
            if (handled.contains(entry)) continue;
            boolean sideDir = false;
            for (String d : SIDE_ASSET_DIRS) {
                if (entry.startsWith(d)) { sideDir = true; break; }
            }
            if (!sideDir) continue;

            String file = entry.substring(entry.lastIndexOf('/') + 1);
            if (!SIDE_SOURCE_TOKEN.matcher(file).find()) continue;
            if (DARK_TOKEN.matcher(file).find()) continue;

            plan.unhandledSideSources.add(entry);
        }
        Collections.sort(plan.unhandledSideSources);

        return plan;
    }
}
