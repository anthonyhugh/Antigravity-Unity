# Antigravity IDE Support for Unity

The **Antigravity IDE Support** package integrates the **Antigravity IDE** (AI-First Code Editor) as a first-class external script editor for Unity. It provides seamless file opening, robust project generation, and bi-directional communication for optimal development workflow.

## Features

- **Project Generation**: Automatically generates `.csproj` and `.sln` files tailored for Antigravity, ensuring full Intellisense and Language Server Protocol (LSP) support.
- **Smart Opening**: Opens files at the exact line and column number.
- **Bi-Directional Messaging**: Supports "Attach to Unity" debugging (where supported) and synchronizes play/pause states.
- **Test Runner Integration**: Run and debug Unity tests directly from the IDE.
- **Preferences Integration**: Fully configurable via **Edit > Preferences > External Tools**.

## Installation

### Via Package Manager (Git URL)

1.  Open **Unity**.
2.  Navigate to **Window > Package Manager**.
3.  Click the **+** (plus) icon in the top-left corner.
4.  Select **Add package from git URL...**.
5.  Enter the repository URL: `https://github.com/humza-13/Antigravity-Unity.git`
6.  Click **Add**.

## Usage

1.  Open **Unity**.
2.  Go to **Edit > Preferences** (Windows) or **Unity > Preferences** (macOS).
3.  Select **External Tools** from the sidebar.
4.  In the **External Script Editor** dropdown, select **Antigravity**.
    - If **Antigravity** does not appear, click **Browse...** and navigate to your Antigravity installation executable (`Antigravity.exe` on Windows, `/Applications/Antigravity.app` on macOS).
5.  Check **Generate .csproj files** for the packages you want to include (e.g., Embedded packages, Local packages).
6.  Click **Regenerate project files** to ensure your solution is up-to-date.

## Configuration

In **External Tools**, you can toggle specialized settings:

- **Reuse existing Antigravity window**: When checked, opening a script will attempt to use an already running instance of Antigravity instead of spawning a new window.

## Troubleshooting

- **"Antigravity not found"**: Ensure Antigravity is installed in the standard location (`/Applications/Antigravity.app` or `%LOCALAPPDATA%\Programs\Antigravity`). If installed successfully but not detected, verify the path or select it manually.
- **Intellisense not working**: Verify that `.csproj` files are being generated. Go to **Preferences > External Tools** and make sure the relevant "Generate .csproj files for..." checkboxes are selected, then click **Regenerate project files**.

