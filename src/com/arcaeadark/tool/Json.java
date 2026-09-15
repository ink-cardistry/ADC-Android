package com.arcaeadark.tool;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Minimal dependency-free JSON reader / writer.
 * Values map to LinkedHashMap, ArrayList, String, Long / Double, Boolean, null.
 * Hand written so the tool runs on plain Android and on a desktop JVM test harness.
 */
public final class Json {
    private final String s;
    private int i;

    private Json(String s) { this.s = s; }

    public static Object parse(String text) {
        Json p = new Json(text);
        p.ws();
        Object v = p.value();
        p.ws();
        if (p.i < p.s.length()) throw p.err("unexpected trailing content");
        return v;
    }

    @SuppressWarnings("unchecked")
    public static Map<String, Object> parseObject(String text) {
        Object v = parse(text);
        if (!(v instanceof Map)) throw new IllegalArgumentException("JSON root is not an object");
        return (Map<String, Object>) v;
    }

    private IllegalArgumentException err(String m) {
        return new IllegalArgumentException("JSON error at " + i + ": " + m);
    }

    private void ws() {
        while (i < s.length()) {
            char c = s.charAt(i);
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
            else break;
        }
    }

    private Object value() {
        ws();
        if (i >= s.length()) throw err("unexpected end of input");
        char c = s.charAt(i);
        switch (c) {
            case '{': return obj();
            case '[': return arr();
            case '"': return str();
            case 't': keyword("true"); return Boolean.TRUE;
            case 'f': keyword("false"); return Boolean.FALSE;
            case 'n': keyword("null"); return null;
            default: return num();
        }
    }

    private void keyword(String k) {
        if (!s.startsWith(k, i)) throw err("invalid literal");
        i += k.length();
    }

    private Map<String, Object> obj() {
        Map<String, Object> m = new LinkedHashMap<String, Object>();
        i++;
        ws();
        if (i < s.length() && s.charAt(i) == '}') { i++; return m; }
        while (true) {
            ws();
            if (i >= s.length() || s.charAt(i) != '"') throw err("expected string key");
            String k = str();
            ws();
            if (i >= s.length() || s.charAt(i) != ':') throw err("expected ':'");
            i++;
            m.put(k, value());
            ws();
            if (i >= s.length()) throw err("unterminated object");
            char c = s.charAt(i);
            if (c == ',') { i++; continue; }
            if (c == '}') { i++; return m; }
            throw err("expected ',' or '}'");
        }
    }

    private List<Object> arr() {
        List<Object> a = new ArrayList<Object>();
        i++;
        ws();
        if (i < s.length() && s.charAt(i) == ']') { i++; return a; }
        while (true) {
            a.add(value());
            ws();
            if (i >= s.length()) throw err("unterminated array");
            char c = s.charAt(i);
            if (c == ',') { i++; continue; }
            if (c == ']') { i++; return a; }
            throw err("expected ',' or ']'");
        }
    }

    private String str() {
        StringBuilder sb = new StringBuilder();
        i++;
        while (true) {
            if (i >= s.length()) throw err("unterminated string");
            char c = s.charAt(i++);
            if (c == '"') return sb.toString();
            if (c == '\\') {
                if (i >= s.length()) throw err("bad escape");
                char e = s.charAt(i++);
                switch (e) {
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    case 'b': sb.append('\b'); break;
                    case 'f': sb.append('\f'); break;
                    case 'n': sb.append('\n'); break;
                    case 'r': sb.append('\r'); break;
                    case 't': sb.append('\t'); break;
                    case 'u':
                        if (i + 4 > s.length()) throw err("bad unicode escape");
                        sb.append((char) Integer.parseInt(s.substring(i, i + 4), 16));
                        i += 4;
                        break;
                    default: throw err("bad escape");
                }
            } else {
                sb.append(c);
            }
        }
    }

    private Object num() {
        int start = i;
        if (i < s.length() && (s.charAt(i) == '-' || s.charAt(i) == '+')) i++;
        boolean frac = false, exp = false;
        while (i < s.length()) {
            char c = s.charAt(i);
            if (c >= '0' && c <= '9') { i++; }
            else if (c == '.' && !frac && !exp) { frac = true; i++; }
            else if ((c == 'e' || c == 'E') && !exp) {
                exp = true; i++;
                if (i < s.length() && (s.charAt(i) == '+' || s.charAt(i) == '-')) i++;
            } else break;
        }
        String t = s.substring(start, i);
        if (t.isEmpty()) throw err("invalid number");
        if (!frac && !exp) {
            try { return Long.valueOf(Long.parseLong(t)); } catch (NumberFormatException ignore) { }
        }
        return Double.valueOf(Double.parseDouble(t));
    }

    public static String write(Object v) {
        StringBuilder sb = new StringBuilder();
        writeValue(sb, v, 0, true);
        return sb.toString();
    }

    @SuppressWarnings("unchecked")
    private static void writeValue(StringBuilder sb, Object v, int indent, boolean pretty) {
        if (v == null) { sb.append("null"); return; }
        if (v instanceof String) { writeString(sb, (String) v); return; }
        if (v instanceof Boolean || v instanceof Number) { sb.append(v.toString()); return; }
        if (v instanceof Map) {
            Map<String, Object> m = (Map<String, Object>) v;
            if (m.isEmpty()) { sb.append("{}"); return; }
            sb.append('{');
            boolean first = true;
            for (Map.Entry<String, Object> e : m.entrySet()) {
                if (!first) sb.append(',');
                first = false;
                if (pretty) { sb.append('\n'); pad(sb, indent + 1); }
                writeString(sb, e.getKey());
                sb.append(':');
                if (pretty) sb.append(' ');
                writeValue(sb, e.getValue(), indent + 1, pretty);
            }
            if (pretty) { sb.append('\n'); pad(sb, indent); }
            sb.append('}');
            return;
        }
        if (v instanceof List) {
            List<Object> a = (List<Object>) v;
            if (a.isEmpty()) { sb.append("[]"); return; }
            sb.append('[');
            boolean first = true;
            for (Object o : a) {
                if (!first) sb.append(',');
                first = false;
                if (pretty) { sb.append('\n'); pad(sb, indent + 1); }
                writeValue(sb, o, indent + 1, pretty);
            }
            if (pretty) { sb.append('\n'); pad(sb, indent); }
            sb.append(']');
            return;
        }
        writeString(sb, v.toString());
    }

    private static void pad(StringBuilder sb, int n) {
        for (int k = 0; k < n; k++) sb.append("  ");
    }

    private static void writeString(StringBuilder sb, String s) {
        sb.append('"');
        for (int k = 0; k < s.length(); k++) {
            char c = s.charAt(k);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
            }
        }
        sb.append('"');
    }
}
