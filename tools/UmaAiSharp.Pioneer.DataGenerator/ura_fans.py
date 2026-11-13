from collections import defaultdict
from functools import lru_cache
import sqlite3


def read_ura_fan_basis(master):
    with sqlite3.connect(master.resolve().as_uri() + '?mode=ro', uri=True) as db:
        first_place = dict(db.execute('''
            SELECT 74 + 2 * (g.race_group_id - 10001), MIN(f.fan_count)
            FROM single_mode_race_group g
            JOIN single_mode_program p ON p.id=g.race_program_id
            JOIN single_mode_fan_count f ON f.fan_set_id=p.fan_set_id AND f."order"=1
            WHERE g.race_group_id BETWEEN 10001 AND 10003
            GROUP BY g.race_group_id HAVING MIN(f.fan_count)=MAX(f.fan_count)
        '''))
        cards = {row[0]: row[1:] for row in db.execute(
            'SELECT id,rarity,effect_table_id,unique_effect_id FROM support_card_data')}
        levels = defaultdict(list)
        for rarity, level, experience in db.execute('SELECT rarity,level,total_exp FROM support_card_level ORDER BY level'):
            levels[rarity].append((level, experience))
        effects = {row[0]: [(1 if index == 0 else 5 * index, value)
                            for index, value in enumerate(row[1:]) if value >= 0]
                   for row in db.execute('''
                       SELECT id,init,limit_lv5,limit_lv10,limit_lv15,limit_lv20,limit_lv25,
                           limit_lv30,limit_lv35,limit_lv40,limit_lv45,limit_lv50
                       FROM support_card_effect_table WHERE type=16
                   ''')}
        unique = {row[0]: (row[1], sum(value for kind, value in ((row[2], row[3]), (row[4], row[5])) if kind == 16))
                  for row in db.execute('SELECT id,lv,type_0,value_0,type_1,value_1 FROM support_card_unique_effect')}

    @lru_cache(maxsize=None)
    def fan_bonus(card, exp):
        rarity, table, unique_id = cards[card]
        level = max(level for level, required in levels[rarity] if required <= exp)
        points = effects.get(table, ())
        before = max((point for point in points if point[0] <= level), default=None)
        after = next((point for point in points if point[0] > level), None)
        value = before[1] if before else 0
        if before and after:
            value += int((level - before[0]) * (after[1] - before[1]) / (after[0] - before[0]))
        unlock, extra = unique.get(unique_id, (0, 0))
        return value + (extra if level >= unlock else 0)

    return first_place, fan_bonus
