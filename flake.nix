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
          ];

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
        };

        apps.drive-assistant = {
          type = "app";
          program = toString (pkgs.writeShellScript "drive-assistant-cli" ''
            set -euo pipefail
            cd ${self}
            exec ${pkgs.dotnet-sdk_8}/bin/dotnet run --project DriveAssistant.Cli/DriveAssistant.Cli.csproj -- "$@"
          '');
        };

        apps.default = self.apps.${system}.drive-assistant;
      });
}
