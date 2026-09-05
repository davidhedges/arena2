# frozen_string_literal: true

require "minitest/autorun"
require "tmpdir"
require "fileutils"
require_relative "npc_profile_paths"

class NpcProfilePathsTest < Minitest::Test
  def setup
    @root = Pathname.new(Dir.mktmpdir("arena-npc-path-test"))
  end

  def teardown
    FileUtils.remove_entry(@root)
  end

  def existing(visual_id = "TEST_NPC", guid = "1" * 32)
    target = NpcProfilePaths.target(@root, visual_id, {})
    target.profile_path.dirname.mkpath
    target.profile_path.write("authored profile\n")
    target.meta_path.write("fileFormatVersion: 2\nguid: #{guid}\nNativeFormatImporter:\n  userData: preserve-me\n")
    target
  end

  def row(visual_id, guid)
    "  - visualId: #{visual_id}\n    profile: {fileID: 11400000, guid: #{guid}, type: 2}\n"
  end

  def test_new_profiles_use_visual_id_paths_and_deterministic_guids
    target = NpcProfilePaths.target(@root, "NEW_NPC", {})
    assert_equal @root.join("Assets/Arena/Resources/NpcVisualProfiles/NEW_NPC.asset"), target.profile_path
    assert_equal Digest::SHA256.hexdigest("Arena.NpcVisualProfile/NEW_NPC")[0, 32], target.guid
    refute target.profile_path.exist?
  end

  def test_existing_guid_and_importer_metadata_are_preserved
    old = existing
    target = NpcProfilePaths.target(@root, "TEST_NPC", { "TEST_NPC" => "1" * 32 })
    assert_equal "1" * 32, target.guid
    assert_equal old.meta_path.read, target.existing_meta
    assert_equal "authored profile\n", target.profile_path.read
  end

  def test_missing_referenced_profile_is_rejected
    assert_raises(RuntimeError) { NpcProfilePaths.target(@root, "MISSING", { "MISSING" => "1" * 32 }) }
  end

  def test_guid_disagreement_is_rejected
    existing
    assert_raises(RuntimeError) { NpcProfilePaths.target(@root, "TEST_NPC", { "TEST_NPC" => "2" * 32 }) }
  end

  def test_profile_without_meta_is_rejected
    old = existing
    old.meta_path.delete
    assert_raises(RuntimeError) { NpcProfilePaths.target(@root, "TEST_NPC", {}) }
  end

  def test_meta_without_profile_is_rejected
    old = existing
    old.profile_path.delete
    assert_raises(RuntimeError) { NpcProfilePaths.target(@root, "TEST_NPC", {}) }
  end

  def test_catalog_ids_are_unique_and_have_valid_guids
    text = row("TEST_NPC", "1" * 32)
    assert_equal({ "TEST_NPC" => "1" * 32 }, NpcProfilePaths.catalog_entries(text))
    assert_raises(RuntimeError) { NpcProfilePaths.catalog_entries(text + text) }
    assert_raises(RuntimeError) { NpcProfilePaths.catalog_entries(row("BROKEN", "invalid")) }
  end

  def test_visual_ids_cannot_select_arbitrary_paths
    assert_raises(RuntimeError) { NpcProfilePaths.target(@root, "../TEST_NPC", {}) }
  end
end
