-- ChestCounterFortnite — synchronisation du carnet de coffres.
-- À exécuter une fois dans Supabase → SQL Editor. Le script est sans danger s'il est relancé :
-- il complète aussi une table créée par une version antérieure.

create table if not exists public.chest_counter_state (
    user_id uuid primary key references auth.users (id) on delete cascade,
    data jsonb not null default '{}'::jsonb,
    updated_at timestamptz not null default now(),
    device_id text
);

-- Une ancienne table peut déjà exister avec un autre schéma : on ajoute les colonnes manquantes.
alter table public.chest_counter_state
    add column if not exists user_id uuid,
    add column if not exists data jsonb not null default '{}'::jsonb,
    add column if not exists updated_at timestamptz not null default now(),
    add column if not exists device_id text;

-- La synchronisation utilise user_id comme clé d'upsert (une ligne par compte).
create unique index if not exists chest_counter_state_user_id_key
    on public.chest_counter_state (user_id);

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'chest_counter_state_user_id_fkey'
          and conrelid = 'public.chest_counter_state'::regclass
    ) then
        alter table public.chest_counter_state
            add constraint chest_counter_state_user_id_fkey
            foreign key (user_id) references auth.users (id) on delete cascade;
    end if;
end $$;

alter table public.chest_counter_state enable row level security;

-- Une ligne par utilisateur : lecture et écriture uniquement par son propriétaire.
drop policy if exists "chest_counter_owner" on public.chest_counter_state;
create policy "chest_counter_owner"
    on public.chest_counter_state
    for all
    to authenticated
    using (auth.uid() = user_id)
    with check (auth.uid() = user_id);

-- Le rôle authenticated est celui d'une session Supabase connectée.
-- Les clés anon/publishable ne donnent aucun accès aux lignes sans session.
grant select, insert, update, delete on public.chest_counter_state to authenticated;
