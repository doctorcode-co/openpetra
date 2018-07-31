SELECT a_motivation_group_code_c, a_motivation_detail_code_c, a_motivation_detail_desc_c
FROM PUB_a_motivation_detail
WHERE a_ledger_number_i = ?
AND (a_motivation_group_code_c LIKE ? OR a_motivation_detail_code_c LIKE ?
OR a_motivation_detail_desc_c LIKE ?)
ORDER BY a_motivation_group_code_c, a_motivation_detail_code_c
