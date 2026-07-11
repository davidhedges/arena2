#!/usr/bin/env ruby
# frozen_string_literal: true

require "digest"
require "json"
require "optparse"
require "pathname"
require "set"

ROOT = Pathname.new(__dir__).join("..").expand_path
INVENTORY_PATH = ROOT.join("Logs/npc-appearance-inventory-draft.json")
PROFILE_DIR = ROOT.join("Assets/Arena/Content/NPC/VisualProfiles")
CATALOG_PATH = ROOT.join("Assets/Arena/Resources/NpcVisualCatalog.asset")
PROFILE_SCRIPT_GUID = "4f7e660e5594473cb3f6e13e43c32f4a"
ANIMATION_KEYS = %w[
  idle ready walk run basicAttack spellCastStart spellRelease spellCancel hit death
].freeze

options = { write: false }
OptionParser.new do |parser|
  parser.banner = "Usage: ruby ops/generate-npc-family-profiles.rb --manifest FILE [--write]"
  parser.on("--manifest FILE", "Reviewed family authoring manifest") { |value| options[:manifest] = value }
  parser.on("--write", "Write profile assets, metas, and catalog rows") { options[:write] = true }
end.parse!

abort("--manifest is required") unless options[:manifest]
manifest_path = ROOT.join(options[:manifest]).expand_path
abort("Missing manifest: #{manifest_path}") unless manifest_path.file?
abort("Missing inventory: #{INVENTORY_PATH}") unless INVENTORY_PATH.file?
abort("Missing visual catalog: #{CATALOG_PATH}") unless CATALOG_PATH.file?

manifest = JSON.parse(manifest_path.read)
inventory = JSON.parse(INVENTORY_PATH.read)
family_name = manifest.fetch("source_family_name")
appearances = inventory.fetch("appearances")
  .select { |entry| entry.fetch("family_name") == family_name }
  .sort_by { |entry| entry.fetch("appearance_id_candidate") }
expected_count = Integer(manifest.fetch("expected_appearance_count"))
abort("#{family_name}: expected #{expected_count} appearances, found #{appearances.length}") unless appearances.length == expected_count

def normalized_animations(family_name, animations)
  unknown_keys = animations.keys - ANIMATION_KEYS
  abort("#{family_name}: unknown animation keys: #{unknown_keys.join(", ")}") unless unknown_keys.empty?

  ANIMATION_KEYS.to_h { |key| [key, animations.fetch(key, [])] }
end

base_animations = normalized_animations(family_name, manifest.fetch("animations"))
base_sockets = manifest.fetch("vfx_sockets")
base_reactions = manifest.fetch("status_reactions", [])
appearance_overrides = manifest.fetch("appearance_overrides", {})
appearance_ids = appearances.map { |entry| entry.fetch("appearance_id_candidate") }.to_set
unknown_override_ids = appearance_overrides.keys.reject { |visual_id| appearance_ids.include?(visual_id) }
abort("#{family_name}: unknown appearance overrides: #{unknown_override_ids.join(", ")}") unless unknown_override_ids.empty?

def parse_prefab(path)
  text = path.read
  game_objects = {}
  transforms = {}
  text.scan(/^--- !u!1 &(\d+)\n(.*?)(?=^--- |\z)/m) do |id, block|
    name = block[/^  m_Name: ([^\n]+)/, 1]
    game_objects[id] = name if name
  end
  text.scan(/^--- !u!4 &(\d+)\n(.*?)(?=^--- |\z)/m) do |id, block|
    game_object = block[/^  m_GameObject: \{fileID: (\d+)\}/, 1]
    parent = block[/^  m_Father: \{fileID: (\d+)\}/, 1]
    transforms[id] = [game_object, parent]
  end
  root = transforms.find { |_id, (_game_object, parent)| parent == "0" }
  abort("#{path}: no root Transform") unless root

  transform_paths = transforms.keys.to_set do |transform_id|
    parts = []
    current = transform_id
    while current && current != "0"
      game_object, parent = transforms.fetch(current)
      parts << game_objects[game_object]
      current = parent
    end
    parts.compact.reverse.drop(1).join("/")
  end
  { root_game_object_id: root[1][0], transform_paths: transform_paths }
end

def profile_guid(visual_id)
  Digest::SHA256.hexdigest("Arena.NpcVisualProfile/#{visual_id}")[0, 32]
end

def yaml_string_list(key, values, indent: 4)
  prefix = " " * indent
  return "#{prefix}#{key}: []\n" if values.empty?

  output = +"#{prefix}#{key}:\n"
  values.each { |value| output << "#{prefix}- #{value}\n" }
  output
end

def profile_yaml(profile_name:, prefab_id:, prefab_guid:, primary_animator_path:, presentation_vertical_offset:, animations:, fallback_policy:, sockets:, reactions:)
  output = +<<~YAML
    %YAML 1.1
    %TAG !u! tag:unity3d.com,2011:
    --- !u!114 &11400000
    MonoBehaviour:
      m_ObjectHideFlags: 0
      m_CorrespondingSourceObject: {fileID: 0}
      m_PrefabInstance: {fileID: 0}
      m_PrefabAsset: {fileID: 0}
      m_GameObject: {fileID: 0}
      m_Enabled: 1
      m_EditorHideFlags: 0
      m_Script: {fileID: 11500000, guid: #{PROFILE_SCRIPT_GUID}, type: 3}
      m_Name: #{profile_name}
      m_EditorClassIdentifier:
      prefab: {fileID: #{prefab_id}, guid: #{prefab_guid}, type: 3}
      primaryAnimatorPath: #{primary_animator_path}
  YAML
  if presentation_vertical_offset != 0.0
    output << "  presentationVerticalOffset: #{presentation_vertical_offset}\n"
  end
  output << "  animations:\n"
  ANIMATION_KEYS.each { |key| output << yaml_string_list(key, animations.fetch(key), indent: 4) }
  output << "  hardCrowdControlFallbackPolicy: #{fallback_policy}\n"
  output << "  vfxSockets:\n"
  sockets.each do |socket|
    output << "  - anchor: #{socket.fetch("anchor")}\n"
    output << "    transformPath: #{socket.fetch("transform_path")}\n"
    output << "    fallbackPolicy: #{Integer(socket.fetch("fallback_policy"))}\n"
  end
  if reactions.empty?
    output << "  statusReactions: []\n"
  else
    output << "  statusReactions:\n"
    reactions.each do |reaction|
      output << "  - statusKind: #{reaction.fetch("status_kind")}\n"
      output << "    loop: {fileID: #{reaction.fetch("clip_file_id")}, guid: #{reaction.fetch("clip_guid")}, type: 3}\n"
      output << "    requireHumanoidAvatar: #{reaction.fetch("require_humanoid_avatar") ? 1 : 0}\n"
    end
  end
  output
end

def profile_meta(guid)
  <<~YAML
    fileFormatVersion: 2
    guid: #{guid}
    NativeFormatImporter:
      externalObjects: {}
      mainObjectFileID: 11400000
      userData:
      assetBundleName:
      assetBundleVariant:
  YAML
end

catalog = CATALOG_PATH.read
catalog_rows = []
generated = []
appearances.each do |entry|
  visual_id = entry.fetch("appearance_id_candidate")
  override = appearance_overrides.fetch(visual_id, {})
  animations = normalized_animations(
    family_name,
    base_animations.merge(override.fetch("animations", {}))
  )
  sockets = override.fetch("vfx_sockets", base_sockets)
  reactions = override.fetch("status_reactions", base_reactions)
  fallback_policy = Integer(
    override.fetch(
      "hard_crowd_control_fallback_policy",
      manifest.fetch("hard_crowd_control_fallback_policy")
    )
  )
  presentation_vertical_offset = Float(
    override.fetch(
      "presentation_vertical_offset",
      manifest.fetch("presentation_vertical_offset", 0.0)
    )
  )
  unless presentation_vertical_offset.finite? && presentation_vertical_offset.abs <= 5.0
    abort("#{visual_id}: presentation vertical offset must be finite and within -5 to 5 meters")
  end
  abort("#{visual_id}: LEFT_HAND socket is required") unless sockets.any? { |socket| socket.fetch("anchor") == "LEFT_HAND" }
  abort("#{visual_id}: TARGET socket is required") unless sockets.any? { |socket| socket.fetch("anchor") == "TARGET" }
  prefab_path = ROOT.join(entry.fetch("prefab_path"))
  abort("#{visual_id}: missing prefab #{prefab_path}") unless prefab_path.file?
  abort("#{visual_id}: expected one Animator, found #{entry.fetch("animator_count")}") unless entry.fetch("animator_count") == 1
  abort("#{visual_id}: root motion requires separate review") if entry.fetch("root_motion_enabled") && !manifest["allow_root_motion"]
  warnings = entry.fetch("review_warnings")
  abort("#{visual_id}: unresolved inventory warnings: #{warnings.join("; ")}") unless warnings.empty?

  primary_animator_path = manifest.fetch("primary_animator_path", entry.fetch("primary_animator_path_candidate"))
  abort("#{visual_id}: generator currently requires a root Animator") unless primary_animator_path == "."
  available_states = entry.fetch("controller_states").to_set
  animations.values.flatten.each do |state|
    abort("#{visual_id}: controller state '#{state}' is missing") unless available_states.include?(state)
  end

  prefab = parse_prefab(prefab_path)
  sockets.each do |socket|
    path = socket.fetch("transform_path")
    abort("#{visual_id}: socket path '#{path}' is missing") unless prefab.fetch(:transform_paths).include?(path)
  end
  prefab_guid = Pathname.new("#{prefab_path}.meta").read[/^guid: (\w+)/, 1]
  abort("#{visual_id}: prefab GUID is missing") unless prefab_guid

  source_name = prefab_path.basename(".prefab").to_s
  profile_name = "#{source_name}_VisualProfile"
  profile_path = PROFILE_DIR.join("#{profile_name}.asset")
  meta_path = Pathname.new("#{profile_path}.meta")
  guid = profile_guid(visual_id)
  yaml = profile_yaml(
    profile_name: profile_name,
    prefab_id: prefab.fetch(:root_game_object_id),
    prefab_guid: prefab_guid,
    primary_animator_path: primary_animator_path,
    presentation_vertical_offset: presentation_vertical_offset,
    animations: animations,
    fallback_policy: fallback_policy,
    sockets: sockets,
    reactions: reactions
  )
  meta = profile_meta(guid)

  if profile_path.exist? && profile_path.read != yaml
    abort("#{visual_id}: existing profile differs from reviewed generation: #{profile_path}") unless options[:write]
  end
  if meta_path.exist? && meta_path.read != meta
    abort("#{visual_id}: existing meta differs from deterministic GUID: #{meta_path}")
  end
  if catalog.match?(/^  - visualId: #{Regexp.escape(visual_id)}$/)
    abort("#{visual_id}: catalog row already exists but generated profile is absent") unless profile_path.exist?
  else
    relative_prefab = prefab_path.relative_path_from(ROOT)
    catalog_rows << [
      "  - visualId: #{visual_id}",
      "    profile: {fileID: 11400000, guid: #{guid}, type: 2}",
      "    assetPath: #{relative_prefab}",
      "    prefab: {fileID: #{prefab.fetch(:root_game_object_id)}, guid: #{prefab_guid}, type: 3}",
      "    statusReactions: []"
    ].join("\n")
  end
  generated << [profile_path, yaml, meta_path, meta]
end

puts "Validated #{appearances.length} reviewed #{family_name} appearances."
unless options[:write]
  puts "Dry run only; pass --write to create #{generated.count { |path, _yaml, _meta_path, _meta| !path.exist? }} profiles and #{catalog_rows.length} catalog rows."
  exit 0
end

generated.each do |profile_path, yaml, meta_path, meta|
  profile_path.write(yaml)
  meta_path.write(meta)
  puts "Wrote #{profile_path.relative_path_from(ROOT)}"
end
unless catalog_rows.empty?
  CATALOG_PATH.write("#{catalog.rstrip}\n#{catalog_rows.join("\n")}\n")
  puts "Appended #{catalog_rows.length} rows to #{CATALOG_PATH.relative_path_from(ROOT)}"
end
