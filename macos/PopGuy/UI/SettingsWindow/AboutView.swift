// AboutView.swift
// PopGuy — UI/SettingsWindow
//
// Isolation: @MainActor — implicitly via SWIFT_DEFAULT_ACTOR_ISOLATION=MainActor.

import AppKit
import SwiftUI

struct AboutView: View {

    @ObservedObject var updater: UpdaterController

    // MARK: - Bundle metadata

    private var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
    }
    private var build: String? {
        Bundle.main.infoDictionary?["CFBundleVersion"] as? String
    }
    private var releaseDate: String? {
        Bundle.main.infoDictionary?["PGReleaseDate"] as? String
    }

    // MARK: - Static URLs

    private static let websiteURL     = URL(string: "https://popguy.coprimal.dev")!
    private static let authorURL      = URL(string: "https://coprimal.dev")!
    private static let emailURL       = URL(string: "mailto:contact@coprimal.dev")!
    private static let discussionsURL = URL(string: "https://github.com/Coprimal/PopGuy/discussions")!
    private static let githubURL      = URL(string: "https://github.com/Coprimal/PopGuy")!

    // MARK: - Body

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: SettingsMetrics.cardSpacing) {
                headerCard
                updatesCard
                linksCard
            }
            .padding(SettingsMetrics.pagePadding)
        }
    }

    // MARK: - Header card (logo + version)

    private var headerCard: some View {
        SettingsCard(title: "PopGuy") {
            HStack(spacing: 16) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 64, height: 64)

                VStack(alignment: .leading, spacing: 4) {
                    Text("PopGuy")
                        .font(.title2.weight(.semibold))
                    Text(versionLine)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    Link("github.com/Coprimal/PopGuy", destination: Self.githubURL)
                        .font(.callout)
                }
            }
        }
    }

    static func makeVersionLine(version: String, build: String?, releaseDate: String?) -> String {
        var parts = ["v\(version)"]
        if let b = build, !b.isEmpty { parts.append("(\(b))") }
        if let d = releaseDate, !d.isEmpty { parts.append("· \(d)") }
        return parts.joined(separator: " ")
    }

    private var versionLine: String {
        Self.makeVersionLine(version: version, build: build, releaseDate: releaseDate)
    }

    // MARK: - Updates card

    private var updatesCard: some View {
        SettingsCard(title: "Updates") {
            VStack(alignment: .leading, spacing: SettingsMetrics.contentSpacing) {
                LabeledContent {
                    Toggle("Automatically check for updates", isOn: $updater.automaticallyChecksForUpdates)
                        .toggleStyle(CompactSwitch())
                        .labelsHidden()
                        // Out of a Form the content sits right after the label; keep the switch in a trailing column.
                        .frame(maxWidth: .infinity, alignment: .trailing)
                } label: {
                    Text("Automatically check for updates")
                }

                HStack(spacing: 8) {
                    Button("Check for Updates\u{2026}") {
                        updater.checkForUpdates()
                    }
                    .buttonStyle(RowTextButtonStyle())
                    .disabled(!updater.canCheckForUpdates)

                    if updater.updateAvailable {
                        Label(
                            updater.pendingVersion.map { "Update available — v\($0)" } ?? "Update available",
                            systemImage: "arrow.down.circle.fill"
                        )
                        .font(.callout)
                        .foregroundStyle(.tint)
                    } else {
                        Text("You're on the latest version.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }

                    Spacer(minLength: 0)
                }
            }
        }
    }

    // MARK: - Links card

    private var linksCard: some View {
        SettingsCard(title: "Info") {
            VStack(alignment: .leading, spacing: SettingsMetrics.contentSpacing) {
                row(label: "Author") {
                    Link("Coprimal", destination: Self.authorURL)
                }
                row(label: "Website") {
                    Link("popguy.coprimal.dev", destination: Self.websiteURL)
                }
                row(label: "Contact") {
                    Link("contact@coprimal.dev", destination: Self.emailURL)
                }
                row(label: "Discussions") {
                    Link("GitHub Discussions", destination: Self.discussionsURL)
                }
            }
        }
    }

    @ViewBuilder
    private func row<Content: View>(label: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 8) {
            Text(label)
                .foregroundStyle(.secondary)
                .frame(width: 90, alignment: .leading)
            content()
            Spacer(minLength: 0)
        }
        .font(.callout)
    }
}

// MARK: - Preview

#Preview {
    AboutView(updater: UpdaterController())
        .frame(width: 580, height: 400)
}
