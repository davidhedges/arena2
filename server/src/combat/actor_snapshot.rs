use std::collections::HashMap;

use spacetimedb::{Identity, ReducerContext, Table};

use crate::npcs::{NpcPhysics, NpcState};
use crate::player_physics::PlayerPhysics;
use crate::player_state::PlayerState;

#[allow(unused_imports)]
use crate::npcs::npc_physics as _;
#[allow(unused_imports)]
use crate::npcs::npc_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

const ACTOR_SPATIAL_CELL_SIZE: f32 = 8.0;
const MAX_QUERY_CELLS_PER_AXIS: i32 = 64;

#[derive(Clone, Copy, Debug)]
pub(crate) struct CombatActorSnapshot {
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

impl CombatActorSnapshot {
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

    fn from_npc_rows(state: &NpcState, physics: &NpcPhysics) -> Self {
        Self {
            player_id: state.identity,
            alive: state.alive,
            pos_x: physics.pos_x,
            pos_y: physics.pos_y,
            pos_z: physics.pos_z,
            facing_yaw: physics.yaw,
            grounded: true,
            hit_radius: state.hit_radius,
            hit_height: state.hit_height,
            last_processed_tick: 0,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub(crate) struct CombatActorSnapshotSet {
    actors: Vec<CombatActorSnapshot>,
    index_by_id: HashMap<Identity, usize>,
    spatial_index: CombatActorSpatialIndex,
}

impl CombatActorSnapshotSet {
    pub(crate) fn collect(ctx: &ReducerContext) -> Self {
        let physics_by_id: HashMap<Identity, PlayerPhysics> = ctx
            .db
            .player_physics()
            .iter()
            .map(|physics| (physics.identity, physics))
            .collect();

        let mut actors = Vec::new();
        let mut index_by_id = HashMap::new();
        for state in ctx.db.player_state().iter() {
            let Some(physics) = physics_by_id.get(&state.player_id) else {
                continue;
            };
            let index = actors.len();
            actors.push(CombatActorSnapshot::from_rows(&state, physics));
            index_by_id.insert(state.player_id, index);
        }

        let npc_physics_by_id: HashMap<Identity, NpcPhysics> = ctx
            .db
            .npc_physics()
            .iter()
            .map(|physics| (physics.identity, physics))
            .collect();

        for state in ctx.db.npc_state().iter() {
            let Some(physics) = npc_physics_by_id.get(&state.identity) else {
                continue;
            };
            let index = actors.len();
            actors.push(CombatActorSnapshot::from_npc_rows(&state, physics));
            index_by_id.insert(state.identity, index);
        }

        let spatial_index = CombatActorSpatialIndex::build(&actors);

        Self {
            actors,
            index_by_id,
            spatial_index,
        }
    }

    pub(crate) fn as_slice(&self) -> &[CombatActorSnapshot] {
        &self.actors
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
            &self.actors,
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
            .query_disc(&self.actors, center_x, center_z, radius_padding, out);
    }

    fn into_actors(self) -> Vec<CombatActorSnapshot> {
        self.actors
    }
}

#[derive(Clone, Debug, Default)]
struct CombatActorSpatialIndex {
    buckets: HashMap<(i32, i32), Vec<usize>>,
    max_hit_radius: f32,
}

impl CombatActorSpatialIndex {
    fn build(actors: &[CombatActorSnapshot]) -> Self {
        let mut buckets: HashMap<(i32, i32), Vec<usize>> = HashMap::new();
        let mut max_hit_radius = 0.0f32;

        for (index, actor) in actors.iter().enumerate() {
            if !actor.pos_x.is_finite() || !actor.pos_z.is_finite() {
                continue;
            }
            max_hit_radius = max_hit_radius.max(actor.hit_radius.max(0.0));
            buckets
                .entry(spatial_cell(actor.pos_x, actor.pos_z))
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
        actors: &[CombatActorSnapshot],
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
            out.extend(0..actors.len());
            return;
        }

        let inflate = radius_padding.max(0.0) + self.max_hit_radius;
        self.query_bounds(
            actors,
            start_x.min(end_x) - inflate,
            start_z.min(end_z) - inflate,
            start_x.max(end_x) + inflate,
            start_z.max(end_z) + inflate,
            out,
        );
    }

    fn query_disc(
        &self,
        actors: &[CombatActorSnapshot],
        center_x: f32,
        center_z: f32,
        radius_padding: f32,
        out: &mut Vec<usize>,
    ) {
        if !center_x.is_finite() || !center_z.is_finite() {
            out.clear();
            out.extend(0..actors.len());
            return;
        }

        let inflate = radius_padding.max(0.0) + self.max_hit_radius;
        self.query_bounds(
            actors,
            center_x - inflate,
            center_z - inflate,
            center_x + inflate,
            center_z + inflate,
            out,
        );
    }

    fn query_bounds(
        &self,
        actors: &[CombatActorSnapshot],
        min_x: f32,
        min_z: f32,
        max_x: f32,
        max_z: f32,
        out: &mut Vec<usize>,
    ) {
        out.clear();
        if actors.is_empty() {
            return;
        }
        if !min_x.is_finite() || !min_z.is_finite() || !max_x.is_finite() || !max_z.is_finite() {
            out.extend(0..actors.len());
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
            out.extend(0..actors.len());
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
        (x / ACTOR_SPATIAL_CELL_SIZE).floor() as i32,
        (z / ACTOR_SPATIAL_CELL_SIZE).floor() as i32,
    )
}

pub(crate) fn actor_snapshot_for(
    ctx: &ReducerContext,
    player_id: Identity,
) -> Option<CombatActorSnapshot> {
    if let Some(state) = ctx.db.player_state().player_id().find(player_id) {
        let physics = ctx.db.player_physics().identity().find(player_id)?;
        return Some(CombatActorSnapshot::from_rows(&state, &physics));
    }

    let state = ctx.db.npc_state().identity().find(player_id)?;
    let physics = ctx.db.npc_physics().identity().find(player_id)?;
    Some(CombatActorSnapshot::from_npc_rows(&state, &physics))
}

pub(crate) fn collect_actor_snapshots(ctx: &ReducerContext) -> Vec<CombatActorSnapshot> {
    CombatActorSnapshotSet::collect(ctx).into_actors()
}

#[cfg(test)]
mod tests {
    use super::{
        CombatActorSnapshot, CombatActorSnapshotSet, CombatActorSpatialIndex, NpcPhysics, NpcState,
    };
    use std::collections::HashMap;

    use spacetimedb::Identity;

    fn identity(byte: u8) -> Identity {
        Identity::from_hex(format!("{byte:064x}").as_str())
            .expect("test identity hex should be valid")
    }

    fn snapshot(id: u8, pos_x: f32, pos_z: f32) -> CombatActorSnapshot {
        CombatActorSnapshot {
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

    fn snapshot_set(actors: Vec<CombatActorSnapshot>) -> CombatActorSnapshotSet {
        let index_by_id = actors
            .iter()
            .enumerate()
            .map(|(index, actor)| (actor.player_id, index))
            .collect::<HashMap<_, _>>();
        let spatial_index = CombatActorSpatialIndex::build(&actors);
        CombatActorSnapshotSet {
            actors,
            index_by_id,
            spatial_index,
        }
    }

    #[test]
    fn segment_query_returns_only_nearby_actors() {
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
            .map(|actor| actor.player_id)
            .collect::<Vec<_>>();
        assert!(ids.contains(&identity(1)));
        assert!(ids.contains(&identity(2)));
        assert!(!ids.contains(&identity(3)));
    }

    #[test]
    fn huge_or_invalid_query_falls_back_to_all_actors() {
        let set = snapshot_set(vec![snapshot(1, 1.0, 0.0), snapshot(2, 40.0, 40.0)]);
        let mut indices = Vec::new();

        set.query_segment_indices(f32::NAN, 0.0, 1.0, 0.0, 0.25, &mut indices);

        assert_eq!(indices.len(), 2);
    }

    #[test]
    fn disc_query_returns_only_nearby_actors() {
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
            .map(|actor| actor.player_id)
            .collect::<Vec<_>>();
        assert!(ids.contains(&identity(1)));
        assert!(ids.contains(&identity(2)));
        assert!(!ids.contains(&identity(3)));
    }

    #[test]
    fn npc_rows_map_to_combat_snapshot() {
        let state = NpcState {
            identity: identity(9),
            alive: true,
            hp: 42,
            max_hp: 100,
            hit_radius: 0.45,
            hit_height: 1.35,
        };
        let physics = NpcPhysics {
            identity: identity(9),
            pos_x: 3.0,
            pos_y: 0.25,
            pos_z: -7.0,
            yaw: 1.25,
            updated_at: spacetimedb::Timestamp::UNIX_EPOCH,
        };

        let snapshot = CombatActorSnapshot::from_npc_rows(&state, &physics);

        assert_eq!(snapshot.player_id, state.identity);
        assert!(snapshot.alive);
        assert_eq!(snapshot.pos_x, physics.pos_x);
        assert_eq!(snapshot.pos_y, physics.pos_y);
        assert_eq!(snapshot.pos_z, physics.pos_z);
        assert_eq!(snapshot.facing_yaw, physics.yaw);
        assert!(snapshot.grounded);
        assert_eq!(snapshot.hit_radius, state.hit_radius);
        assert_eq!(snapshot.hit_height, state.hit_height);
    }
}
