{
  description = "Drive Assistant development shell and CLI runner";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
      in
      {
        devShells.default = pkgs.mkShell {
          packages = [
            pkgs.dotnet-sdk_8
            pkgs.git
            pkgs.fontconfig
            pkgs.libGL
            pkgs.xorg.libICE
            pkgs.xorg.libSM
            pkgs.xorg.libX11
            pkgs.xorg.libXcursor
            pkgs.xorg.libXi
            pkgs.xorg.libXrandr
          ];

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [
            pkgs.fontconfig
            pkgs.libGL
            pkgs.xorg.libICE
            pkgs.xorg.libSM
            pkgs.xorg.libX11
            pkgs.xorg.libXcursor
            pkgs.xorg.libXi
            pkgs.xorg.libXrandr
          ];
        };

        apps.drive-assistant-desktop = {
          type = "app";
          program = toString (pkgs.writeShellScript "drive-assistant-desktop" ''
            set -euo pipefail
            export LD_LIBRARY_PATH="${pkgs.lib.makeLibraryPath [
              pkgs.fontconfig
              pkgs.libGL
              pkgs.xorg.libICE
              pkgs.xorg.libSM
              pkgs.xorg.libX11
              pkgs.xorg.libXcursor
              pkgs.xorg.libXi
              pkgs.xorg.libXrandr
            ]}:''${LD_LIBRARY_PATH:-}"
            cd ${self}
            exec ${pkgs.dotnet-sdk_8}/bin/dotnet run --project DriveAssistant.Avalonia/DriveAssistant.Avalonia.csproj -- "$@"
          '');
        };

        apps.drive-assistant = {
          type = "app";
          program = toString (pkgs.writeShellScript "drive-assistant-cli" ''
            set -euo pipefail
            cd ${self}
            exec ${pkgs.dotnet-sdk_8}/bin/dotnet run --project DriveAssistant.Cli/DriveAssistant.Cli.csproj -- "$@"
          '');
        };

        apps.default = self.apps.${system}.drive-assistant-desktop;
      });
}
