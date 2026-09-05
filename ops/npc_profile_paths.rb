# frozen_string_literal: true

require "digest"
require "pathname"

# Current asset destinations and stable-reference checks shared by generation
# and the read-only catalog check. Existing .meta files remain authoritative.
module NpcProfilePaths
  PROFILE_DIRECTORY = "Assets/Arena/Resources/NpcVisualProfiles"
  CATALOG_PATH = "Assets/Arena/Content/NPC/NpcVisualCatalog.asset"
  Target = Struct.new(:profile_path, :meta_path, :guid, :existing_meta)

  def self.catalog_entries(text)
    entries = {}
    text.scan(/^  - visualId: ([^\n]+)\n(.*?)(?=^  - visualId:|\z)/m).each do |visual_id, row|
      raise "Duplicate NPC visual ID: #{visual_id}" if entries.key?(visual_id)
      guid = row[/^    profile: \{fileID: 11400000, guid: ([0-9a-f]{32}), type: 2\}/, 1]
      raise "#{visual_id}: catalog profile GUID is missing or invalid" unless guid
      entries[visual_id] = guid
    end
    raise "NPC visual catalog has no entries" if entries.empty?
    entries
  end

  def self.target(root, visual_id, entries)
    raise "Invalid NPC visual ID: #{visual_id}" unless visual_id.match?(/\A[A-Z0-9_]+\z/)
    profile = root.join(PROFILE_DIRECTORY, "#{visual_id}.asset")
    meta_path = Pathname.new("#{profile}.meta")
    if profile.file? != meta_path.file?
      raise "#{visual_id}: profile and .meta must exist together at #{profile}"
    end
    if entries.key?(visual_id) && !profile.file?
      raise "#{visual_id}: catalog references a missing profile at #{profile}"
    end
    meta = meta_path.file? ? meta_path.read : nil
    guid = if meta
      meta[/^guid: ([0-9a-f]{32})$/, 1]
    else
      Digest::SHA256.hexdigest("Arena.NpcVisualProfile/#{visual_id}")[0, 32]
    end
    raise "#{visual_id}: profile GUID is missing or invalid" unless guid
    if entries.key?(visual_id) && entries[visual_id] != guid
      raise "#{visual_id}: catalog/profile GUID mismatch at #{profile}"
    end
    Target.new(profile, meta_path, guid, meta)
  end
end
