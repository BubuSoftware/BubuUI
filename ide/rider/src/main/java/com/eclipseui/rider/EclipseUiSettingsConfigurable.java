package com.eclipseui.rider;

import com.intellij.openapi.options.Configurable;
import com.intellij.ui.components.JBLabel;
import com.intellij.ui.components.JBTextField;
import com.intellij.util.ui.FormBuilder;
import org.jetbrains.annotations.Nullable;

import javax.swing.JComponent;
import javax.swing.JPanel;

/** Settings | Languages &amp; Frameworks | EclipseUI. */
public final class EclipseUiSettingsConfigurable implements Configurable {

    private JBTextField dotnet;
    private JBTextField serverDll;
    private JBTextField runtimeDll;
    private JBTextField extraAssemblies;

    @Override
    public String getDisplayName() {
        return "EclipseUI";
    }

    @Override
    public @Nullable JComponent createComponent() {
        dotnet = new JBTextField();
        serverDll = new JBTextField();
        runtimeDll = new JBTextField();
        extraAssemblies = new JBTextField();

        JPanel panel = FormBuilder.createFormBuilder()
                .addLabeledComponent("dotnet executable:", dotnet)
                .addLabeledComponent("Server DLL:", serverDll)
                .addLabeledComponent("Runtime DLL (optional):", runtimeDll)
                .addLabeledComponent("Extra catalog assemblies (optional):", extraAssemblies)
                .addComponent(new JBLabel(
                        "<html>Server DLL empty = use the bundled server, then repository build output. "
                                + "Run <code>ide/build.ps1</code> to publish it. "
                                + "Changes take effect after restarting the language server.</html>"))
                .getPanel();
        return panel;
    }

    @Override
    public boolean isModified() {
        EclipseUiSettings.State state = EclipseUiSettings.getInstance().getState();
        return !text(dotnet).equals(value(state.dotnet))
                || !text(serverDll).equals(value(state.serverDll))
                || !text(runtimeDll).equals(value(state.runtimeDll))
                || !text(extraAssemblies).equals(value(state.extraAssemblies));
    }

    @Override
    public void apply() {
        EclipseUiSettings.State state = EclipseUiSettings.getInstance().getState();
        state.dotnet = text(dotnet);
        state.serverDll = text(serverDll);
        state.runtimeDll = text(runtimeDll);
        state.extraAssemblies = text(extraAssemblies);
    }

    @Override
    public void reset() {
        EclipseUiSettings.State state = EclipseUiSettings.getInstance().getState();
        dotnet.setText(value(state.dotnet));
        serverDll.setText(value(state.serverDll));
        runtimeDll.setText(value(state.runtimeDll));
        extraAssemblies.setText(value(state.extraAssemblies));
    }

    private static String text(JBTextField field) {
        return field == null ? "" : field.getText().trim();
    }

    private static String value(String stored) {
        return stored == null ? "" : stored.trim();
    }
}
