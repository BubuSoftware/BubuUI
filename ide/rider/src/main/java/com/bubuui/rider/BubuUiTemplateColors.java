package com.bubuui.rider;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.intellij.openapi.editor.colors.TextAttributesKey;
import com.intellij.openapi.editor.markup.TextAttributes;
import com.intellij.ui.JBColor;
import org.jetbrains.plugins.textmate.language.syntax.lexer.TextMateScope;

import java.awt.Color;
import java.awt.Font;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;

final class BubuUiTemplateColors {
    private record Role(TextAttributesKey key, List<String> scopes) { }
    private static final Map<String, Role> ROLES = load();

    private static Map<String, Role> load() {
        try (var reader = new InputStreamReader(Objects.requireNonNull(
                BubuUiTemplateColors.class.getResourceAsStream("/textmate/highlighting/palette.json")),
                StandardCharsets.UTF_8)) {
            JsonObject palette = JsonParser.parseReader(reader).getAsJsonObject();
            Map<String, Role> roles = new LinkedHashMap<>();
            for (var entry : palette.entrySet()) {
                var value = entry.getValue().getAsJsonObject();
                var color = new JBColor(Color.decode(value.get("light").getAsString()),
                        Color.decode(value.get("dark").getAsString()));
                var key = TextAttributesKey.createTextAttributesKey("BUBUUI_" + entry.getKey().toUpperCase(java.util.Locale.ROOT),
                        new TextAttributes(color, null, null, null, Font.PLAIN));
                List<String> scopes = new ArrayList<>();
                value.getAsJsonArray("scopes").forEach(scope -> scopes.add(scope.getAsString()));
                roles.put(entry.getKey(), new Role(key, scopes));
            }
            return roles;
        } catch (Exception error) {
            throw new IllegalStateException("Cannot load BubuUI template palette", error);
        }
    }

    static TextAttributesKey forScope(TextMateScope scope) {
        List<String> names = new ArrayList<>();
        for (var current = scope; current != null; current = current.getParent()) {
            if (current.getScopeName() == null) continue;
            String name = current.getScopeName().toString();
            // C# strings, comments and identifiers must retain native/semantic colours.
            if (name.equals("source.cs")) return null;
            names.add(name);
        }
        return forScopes(names);
    }

    // Scopes are ordered from the innermost token out to the root language.
    static TextAttributesKey forScopes(List<String> scopes) {
        if (scopes.contains("source.cs")) return null;
        for (String scope : scopes)
            for (var role : ROLES.values())
                for (String candidate : role.scopes())
                    if (scope.equals(candidate) || scope.startsWith(candidate + ".")) return role.key();
        return null;
    }
}
