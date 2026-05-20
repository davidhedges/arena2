use std::collections::HashMap;

use spacetimedb::{Identity, ReducerContext, Table};

use crate::player_physics::PlayerPhysics;
use crate::player_state::PlayerState;

#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

const PLAYER_SPATIAL_CELL_SIZE: f32 = 8.0;
const MAX_QUERY_CELLS_PER_AXIS: i32 = 64;

#[derive(Clone, Copy, Debug)]
pub(crate) struct PlayerSnapshot {
    pub player_id: Identity,
    pub alive: bool,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub facing_yaw: f32,
    pub grounded: bool,
    pub hit_radius: f32,
    pub hit_height: f32,
    pub last_processed_tick: u32,
}

impl PlayerSnapshot {
    fn from_rows(state: &PlayerState, physics: &PlayerPhysics) -> Self {
        Self {
            player_id: state.player_id,
            alive: state.alive,
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            facing_yaw: physics.yaw,
            grounded: physics.grounded,
            hit_radius: state.hit_radius,
            hit_height: state.hit_height,
            last_processed_tick: physics.last_processed_tick,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub(crate) struct PlayerSnapshotSet {
    players: Vec<PlayerSnapshot>,
    index_by_id: HashMap<Identity, usize>,
    spatial_index: PlayerSpatialIndex,
}

impl PlayerSnapshotSet {
    pub(crate) fn collect(ctx: &ReducerContext) -> Self {
        let physics_by_id: HashMap<Identity, PlayerPhysics> = ctx
            .db
            .player_physics()
            .iter()
            .map(|physics| (physics.identity, physics))
            .collect();

        let mut players = Vec::new();
        let mut index_by_id = HashMap::new();
        for state in ctx.db.player_state().iter() {
            let Some(physics) = physics_by_id.get(&state.player_id) else {
                continue;
            };
            let index = players.len();
            players.push(PlayerSnapshot::from_rows(&state, physics));
            index_by_id.insert(state.player_id, index);
        }

        let spatial_index = PlayerSpatialIndex::build(&players);

        Self {
            players,
            index_by_id,
            spatial_index,
        }
    }

    pub(crate) fn as_slice(&self) -> &[PlayerSnapshot] {
        &self.players
    }

    pub(crate) fn index_by_id(&self) -> &HashMap<Identity, usize> {
        &self.index_by_id
    }

    pub(crate) fn query_segment_indices(
        &self,
        start_x: f32,
        start_z: f32,
        end_x: f32,
        end_z: f32,
        radius_padding: f32,
        out: &mut Vec<usize>,
    ) {
        self.spatial_index.query_segment(
            &self.players,
            start_x,
            start_z,
            end_x,
            end_z,
            radius_padding,
            out,
        );
    }

    pub(crate) fn query_disc_indices(
        &self,
        center_x: f32,
        center_z: f32,
        radius_padding: f32,
        out: &mut Vec<usize>,
    ) {
        self.spatial_index
            .query_disc(&self.players, center_x, center_z, radius_padding, out);
    }

    fn into_players(self) -> Vec<PlayerSnapshot> {
        self.players
    }
}

#[derive(Clone, Debug, Default)]
struct PlayerSpatialIndex {
    buckets: HashMap<(i32, i32), Vec<usize>>,
    max_hit_radius: f32,
}

impl PlayerSpatialIndex {
    fn build(players: &[PlayerSnapshot]) -> Self {
        let mut buckets: HashMap<(i32, i32), Vec<usize>> = HashMap::new();
        let mut max_hit_radius = 0.0f32;

        for (index, player) in players.iter().enumerate() {
            if !player.pos_x.is_finite() || !player.pos_z.is_finite() {
                continue;
            }
            max_hit_radius = max_hit_radius.max(player.hit_radius.max(0.0));
            buckets
                .entry(spatial_cell(player.pos_x, player.pos_z))
                .or_default()
                .push(index);
        }

        Self {
            buckets,
            max_hit_radius,
        }
    }

    fn query_segment(
        &self,
        players: &[PlayerSnapshot],
        start_x: f32,
        start_z: f32,
        end_x: f32,
        end_z: f32,
        radius_padding: f32,
        out: &mut Vec<usize>,
    ) {
        if !start_x.is_finite() || !start_z.is_finite() || !end_x.is_finite() || !end_z.is_finite()
        {
            out.clear();
            out.extend(0..players.len());
            return;
        }

        let inflate = radius_padding.max(0.0) + self.max_hit_radius;
        self.query_bounds(
            players,
            start_x.min(end_x) - inflate,
            start_z.min(end_z) - inflate,
            start_x.max(end_x) + inflate,
            start_z.max(end_z) + inflate,
            out,
        );
    }

    fn query_disc(
        &self,
        players: &[PlayerSnapshot],
        center_x: f32,
        center_z: f32,
        radius_padding: f32,
        out: &mut Vec<usize>,
    ) {
        if !center_x.is_finite() || !center_z.is_finite() {
            out.clear();
            out.extend(0..players.len());
            return;
        }

        let inflate = radius_padding.max(0.0) + self.max_hit_radius;
        self.query_bounds(
            players,
            center_x - inflate,
            center_z - inflate,
            center_x + inflate,
            center_z + inflate,
            out,
        );
    }

    fn query_bounds(
        &self,
        players: &[PlayerSnapshot],
        min_x: f32,
        min_z: f32,
        max_x: f32,
        max_z: f32,
        out: &mut Vec<usize>,
    ) {
        out.clear();
        if players.is_empty() {
            return;
        }
        if !min_x.is_finite() || !min_z.is_finite() || !max_x.is_finite() || !max_z.is_finite() {
            out.extend(0..players.len());
            return;
        }

        let min_cell = spatial_cell(min_x, min_z);
        let max_cell = spatial_cell(max_x, max_z);
        let cells_x = max_cell.0 - min_cell.0 + 1;
        let cells_z = max_cell.1 - min_cell.1 + 1;
        if cells_x <= 0
            || cells_z <= 0
            || cells_x > MAX_QUERY_CELLS_PER_AXIS
            || cells_z > MAX_QUERY_CELLS_PER_AXIS
        {
            out.extend(0..players.len());
            return;
        }

        for cell_x in min_cell.0..=max_cell.0 {
            for cell_z in min_cell.1..=max_cell.1 {
                if let Some(indices) = self.buckets.get(&(cell_x, cell_z)) {
                    out.extend(indices.iter().copied());
                }
            }
        }
    }
}

fn spatial_cell(x: f32, z: f32) -> (i32, i32) {
    (
        (x / PLAYER_SPATIAL_CELL_SIZE).floor() as i32,
        (z / PLAYER_SPATIAL_CELL_SIZE).floor() as i32,
    )
}

pub(crate) fn player_snapshot_for(
    ctx: &ReducerContext,
    player_id: Identity,
) -> Option<PlayerSnapshot> {
    let state = ctx.db.player_state().player_id().find(player_id)?;
    let physics = ctx.db.player_physics().identity().find(player_id)?;
    Some(PlayerSnapshot::from_rows(&state, &physics))
}

pub(crate) fn collect_player_snapshots(ctx: &ReducerContext) -> Vec<PlayerSnapshot> {
    PlayerSnapshotSet::collect(ctx).into_players()
}

#[cfg(test)]
mod tests {
    use super::{PlayerSnapshot, PlayerSnapshotSet, PlayerSpatialIndex};
    use std::collections::HashMap;

    use spacetimedb::Identity;

    fn identity(byte: u8) -> Identity {
        Identity::from_hex(format!("{byte:064x}").as_str())
            .expect("test identity hex should be valid")
    }

    fn snapshot(id: u8, pos_x: f32, pos_z: f32) -> PlayerSnapshot {
        PlayerSnapshot {
            player_id: identity(id),
            alive: true,
            pos_x,
            pos_y: 0.0,
            pos_z,
            facing_yaw: 0.0,
            grounded: true,
            hit_radius: 0.5,
            hit_height: 2.0,
            last_processed_tick: 0,
        }
    }

    fn snapshot_set(players: Vec<PlayerSnapshot>) -> PlayerSnapshotSet {
        let index_by_id = players
            .iter()
            .enumerate()
            .map(|(index, player)| (player.player_id, index))
            .collect::<HashMap<_, _>>();
        let spatial_index = PlayerSpatialIndex::build(&players);
        PlayerSnapshotSet {
            players,
            index_by_id,
            spatial_index,
        }
    }

    #[test]
    fn segment_query_returns_only_nearby_players() {
        let set = snapshot_set(vec![
            snapshot(1, 1.0, 0.5),
            snapshot(2, 4.0, -0.5),
            snapshot(3, 40.0, 40.0),
        ]);
        let mut indices = Vec::new();

        set.query_segment_indices(0.0, 0.0, 5.0, 0.0, 0.25, &mut indices);

        let ids = indices
            .iter()
            .filter_map(|index| set.as_slice().get(*index))
            .map(|player| player.player_id)
            .collect::<Vec<_>>();
        assert!(ids.contains(&identity(1)));
        assert!(ids.contains(&identity(2)));
        assert!(!ids.contains(&identity(3)));
    }

    #[test]
    fn huge_or_invalid_query_falls_back_to_all_players() {
        let set = snapshot_set(vec![snapshot(1, 1.0, 0.0), snapshot(2, 40.0, 40.0)]);
        let mut indices = Vec::new();

        set.query_segment_indices(f32::NAN, 0.0, 1.0, 0.0, 0.25, &mut indices);

        assert_eq!(indices.len(), 2);
    }

    #[test]
    fn disc_query_returns_only_nearby_players() {
        let set = snapshot_set(vec![
            snapshot(1, 10.0, 10.0),
            snapshot(2, 12.0, 10.0),
            snapshot(3, 40.0, 40.0),
        ]);
        let mut indices = Vec::new();

        set.query_disc_indices(10.0, 10.0, 1.0, &mut indices);

        let ids = indices
            .iter()
            .filter_map(|index| set.as_slice().get(*index))
            .map(|player| player.player_id)
            .collect::<Vec<_>>();
        assert!(ids.contains(&identity(1)));
        assert!(ids.contains(&identity(2)));
        assert!(!ids.contains(&identity(3)));
    }
}
